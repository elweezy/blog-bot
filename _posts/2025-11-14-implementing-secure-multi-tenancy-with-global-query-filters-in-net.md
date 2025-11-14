---
layout: post
title: "Implementing Secure Multi-Tenancy with Global Query Filters in .NET"
date: 2025-11-14 03:23:20 +0000
categories: dotnet blog
canonical_url: "https://dev.to/gigaherz/building-a-secure-dal-composable-multi-tenancy-filtering-with-c-and-linq2db-19lo"
---

Multi-tenant data isolation needs to be baked into the data access layer, not bolted on haphazardly through manual query predicates. And for modern .NET applications leveraging Entity Framework Core, Global Query Filters have emerged as a powerful, elegant, and indeed, _secure_ way to achieve this.

### Why Data Isolation is Non-Negotiable in SaaS

The SaaS landscape thrives on efficiency. Sharing infrastructure—like a single database—across multiple tenants significantly reduces operational overhead and cost compared to provisioning a dedicated database for each customer. But this efficiency comes with a monumental responsibility: absolute data isolation. A leak between tenants isn't just a bug; it's a catastrophic security breach that can obliterate trust and ruin a business.

Traditional approaches often involve injecting `WHERE TenantId = @tenantId` clauses into every single query manually, or resorting to more complex patterns like database views or separate schemas. The former is brittle, as my anecdote painfully illustrates. The latter often introduces its own complexities in deployment, schema migrations, and ORM integration, sometimes sacrificing the very cost efficiency we seek.

This is where EF Core's Global Query Filters shine. They offer a declarative, centralized mechanism to apply tenant-specific filtering _automatically_ to every query for a given entity type, right at the ORM level. This isn't just a convenience; it's a security paradigm shift, making data isolation a default, not an optional step.

### Diving Deep: Global Query Filters and the Multi-Tenant Context

At its core, a Global Query Filter is a LINQ expression that EF Core automatically appends to _any_ query involving the configured entity type. You define it once, usually in your `DbContext`'s `OnModelCreating` method, and EF Core takes care of the rest.

For multi-tenancy, the filter typically looks something like `e => e.TenantId == _tenantId`. The challenge, then, becomes how to reliably provide that `_tenantId` to the `DbContext` instance that's processing the query. This requires a bit of thoughtful plumbing.

First, we need a mechanism to identify the current tenant. In a typical web application, this information lives in the `HttpContext`, often derived from an API key, a JWT claim, or a subdomain. For background services, it might come from a message payload or a scoped operation context.

Let's define a simple interface for abstracting tenant identification:

```csharp
namespace MyApp.Core.TenantManagement;

public interface ITenantProvider
{
    Guid? GetCurrentTenantId();
}
```

Now, how do we get this `ITenantProvider` into our `DbContext` and use it within `OnModelCreating`? We leverage Dependency Injection.

### Code Example: Building a Secure, Filtered Data Access Layer

Let's walk through a practical example demonstrating how to wire all of this together using a minimal API, a custom `ITenantProvider`, and EF Core's Global Query Filters.

First, our foundational entity and `ITenantProvider` implementation:

```csharp
// Entities/TenantAwareEntity.cs
using System;

namespace MyApp.Data.Entities;

public abstract class TenantAwareEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } // Foreign key to a Tenant entity, implicitly
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedUtc { get; set; }
}

public class Product : TenantAwareEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int QuantityAvailable { get; set; }
}

// Services/TenantProvider.cs
using Microsoft.AspNetCore.Http;
using MyApp.Core.TenantManagement;
using System;
using System.Security.Claims;

namespace MyApp.Services;

public class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HttpContextTenantProvider> _logger;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor, ILogger<HttpContextTenantProvider> logger)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Guid? GetCurrentTenantId()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            _logger.LogWarning("Attempted to get tenant ID outside of HTTP context.");
            return null; // Or throw an exception, depending on your policy for non-HTTP contexts
        }

        // Assuming Tenant ID is stored as a claim, e.g., during JWT authentication
        var tenantIdClaim = httpContext.User.FindFirst("tenant_id");
        if (tenantIdClaim != null && Guid.TryParse(tenantIdClaim.Value, out var tenantId))
        {
            _logger.LogDebug("Tenant ID '{TenantId}' retrieved from HTTP context.", tenantId);
            return tenantId;
        }

        _logger.LogWarning("Tenant ID claim not found or invalid in HTTP context.");
        return null;
    }
}
```

_Why this way?_ `IHttpContextAccessor` is the standard way to get access to the current `HttpContext` in ASP.NET Core. Using `ClaimsPrincipal` is robust for authenticated scenarios. For background jobs, you might use an `AsyncLocal<Guid?>` to flow the tenant ID explicitly. The `ILogger` is crucial for debugging and understanding _why_ a tenant ID might be missing, which often points to authentication/authorization issues.

Next, our `DbContext` and its configuration:

```csharp
// Data/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using MyApp.Core.TenantManagement;
using MyApp.Data.Entities;
using System;
using System.Linq; // Needed for the Where clause in the filter

namespace MyApp.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<AppDbContext> _logger;

    public AppDbContext(DbContextOptions<AppDbContext> options,
                        ITenantProvider tenantProvider,
                        ILogger<AppDbContext> logger)
        : base(options)
    {
        _tenantProvider = tenantProvider ?? throw new ArgumentNullException(nameof(tenantProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Get the current tenant ID once per DbContext instance
        // This is safe because DbContexts are typically scoped to a request
        var currentTenantId = _tenantProvider.GetCurrentTenantId();

        if (currentTenantId.HasValue)
        {
            _logger.LogInformation("Applying tenant filter for TenantId: {TenantId}", currentTenantId.Value);

            // Apply global query filter for all TenantAwareEntity types
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (typeof(TenantAwareEntity).IsAssignableFrom(entityType.ClrType))
                {
                    // Using a lambda expression with a closure for the currentTenantId
                    // This creates a filter specific to the DbContext's lifespan (and thus the request's tenant)
                    modelBuilder.Entity(entityType.ClrType)
                        .HasQueryFilter(e => EF.Property<Guid>(e, "TenantId") == currentTenantId.Value);
                }
            }
        }
        else
        {
            _logger.LogWarning("No tenant ID available. Global Query Filters for multi-tenancy will NOT be applied.");
            // Decide your policy: either allow unfiltered access (DANGEROUS!) or restrict access entirely.
            // For production, if currentTenantId is null, it typically means an unauthenticated request
            // or a misconfigured tenant provider, and queries should probably fail or return empty.
        }

        // Example: Seed some data if needed (for demonstration)
        // modelBuilder.Entity<Product>().HasData(
        //     new Product { Id = Guid.NewGuid(), Name = "Gizmo A", Price = 10.00m, QuantityAvailable = 100, TenantId = new Guid("YOUR_TENANT_A_GUID") },
        //     new Product { Id = Guid.NewGuid(), Name = "Widget B", Price = 25.50m, QuantityAvailable = 50, TenantId = new Guid("YOUR_TENANT_B_GUID") }
        // );
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var currentTenantId = _tenantProvider.GetCurrentTenantId();
        if (!currentTenantId.HasValue)
        {
            throw new InvalidOperationException("Cannot save changes without a valid tenant context.");
        }

        foreach (var entry in ChangeTracker.Entries<TenantAwareEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId == Guid.Empty) // Ensure tenant ID is set on creation
                {
                    entry.Entity.TenantId = currentTenantId.Value;
                }
                else if (entry.Entity.TenantId != currentTenantId.Value)
                {
                    // PREVENT TENANT IMPERSONATION OR DATA INJECTION
                    _logger.LogError("Attempted to create entity with mismatched TenantId: {ProvidedTenantId} vs {CurrentTenantId}",
                                     entry.Entity.TenantId, currentTenantId.Value);
                    throw new InvalidOperationException("TenantId mismatch on new entity creation.");
                }
            }
            // For existing entities, we generally trust the filter to prevent
            // loading data from other tenants, but you *could* add checks here
            // to prevent malicious updates if an entity was somehow loaded (e.g., IgnoreQueryFilters)
            if (entry.State == EntityState.Modified)
            {
                 // Prevent accidental change of tenant ID on an existing entity
                if (entry.Property(nameof(TenantAwareEntity.TenantId)).IsModified)
                {
                    _logger.LogError("Attempted to change TenantId for entity {EntityId} from {OldTenantId} to {NewTenantId}",
                                     entry.Entity.Id,
                                     entry.OriginalValues[nameof(TenantAwareEntity.TenantId)],
                                     entry.CurrentValues[nameof(TenantAwareEntity.TenantId)]);
                    throw new InvalidOperationException("TenantId cannot be changed.");
                }
                entry.Entity.ModifiedUtc = DateTime.UtcNow;
            }
        }
        return await base.SaveChangesAsync(cancellationToken);
    }
}
```

_Why this way?_ Injecting `ITenantProvider` directly into `AppDbContext` is crucial. The `OnModelCreating` method is where the magic happens: we iterate over all types inheriting `TenantAwareEntity` and apply the filter. Using `EF.Property<Guid>(e, "TenantId")` is a robust way to access properties when using `modelBuilder.Entity(entityType.ClrType)` for dynamically applying filters. The `SaveChanges` override is a _critical_ security hardening step, ensuring that new entities are correctly stamped with the current tenant's ID and preventing accidental (or malicious) data assignment to the wrong tenant. It also prevents changing a `TenantId` on an existing entity, which could effectively "move" data between tenants.

Finally, integrating into an ASP.NET Core minimal API:

```csharp
// Program.cs
using Microsoft.EntityFrameworkCore;
using MyApp.Core.TenantManagement;
using MyApp.Data;
using MyApp.Data.Entities;
using MyApp.Services;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddLogging(config => config.AddConsole()); // Simple console logging
builder.Services.AddHttpContextAccessor(); // Required for HttpContextTenantProvider
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

// Configure DbContext with SQLite for demonstration
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("MultiTenantDb") // Use in-memory for quick demo
);

var app = builder.Build();

// Seed data (for demonstration purposes only)
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<AppDbContext>();
    // Ensure the database is created
    // context.Database.EnsureCreated(); // For real DBs

    // Create some test tenants
    var tenantAGuid = new Guid("4b1e4c7a-a63b-4e1a-8f6b-7c8d9e0f1a2b"); // Example Tenant A
    var tenantBGuid = new Guid("2a3b4c5d-6e7f-8a9b-0c1d-2e3f4a5b6c7d"); // Example Tenant B

    // Seed products for Tenant A
    if (!context.Products.Any(p => p.TenantId == tenantAGuid))
    {
        context.Products.Add(new Product { Name = "Gizmo Pro", Description = "High-performance gizmo", Price = 99.99m, QuantityAvailable = 10, TenantId = tenantAGuid });
        context.Products.Add(new Product { Name = "Basic Gadget", Description = "Everyday utility gadget", Price = 19.99m, QuantityAvailable = 200, TenantId = tenantAGuid });
        await context.SaveChangesAsync();
    }
    // Seed products for Tenant B
    if (!context.Products.Any(p => p.TenantId == tenantBGuid))
    {
        context.Products.Add(new Product { Name = "Quantum Widget", Description = "Next-gen quantum computing component", Price = 1999.00m, QuantityAvailable = 2, TenantId = tenantBGuid });
        context.Products.Add(new Product { Name = "Simple Item", Description = "A very simple item", Price = 5.00m, QuantityAvailable = 500, TenantId = tenantBGuid });
        await context.SaveChangesAsync();
    }
}

// Minimal API endpoints
app.MapGet("/products", async (AppDbContext db, ILogger<Program> logger) =>
{
    // The Global Query Filter ensures only products for the current tenant are returned
    var products = await db.Products.ToListAsync();
    logger.LogInformation("Retrieved {ProductCount} products for current tenant.", products.Count);
    return products;
});

app.MapPost("/products", async (Product newProduct, AppDbContext db, ILogger<Program> logger) =>
{
    // The SaveChangesAsync override will stamp the TenantId and validate it
    await db.Products.AddAsync(newProduct);
    await db.SaveChangesAsync(); // TenantId will be set automatically or validated

    logger.LogInformation("Added new product '{ProductName}' for tenant {TenantId}.", newProduct.Name, newProduct.TenantId);
    return Results.Created($"/products/{newProduct.Id}", newProduct);
});

// A simple middleware to simulate authentication and set a 'tenant_id' claim for testing
app.Use((context, next) =>
{
    // FOR DEMONSTRATION PURPOSES ONLY: This is not how you'd do auth in production.
    // In production, an actual authentication middleware would populate context.User.
    var tenantIdHeader = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
    if (Guid.TryParse(tenantIdHeader, out var parsedTenantId))
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), // Example user ID
            new Claim("tenant_id", parsedTenantId.ToString())
        };
        var appIdentity = new ClaimsIdentity(claims, "TestAuth");
        context.User = new ClaimsPrincipal(appIdentity);
        context.Items["TenantIdSet"] = true;
    }
    else
    {
        context.Items["TenantIdSet"] = false;
    }
    return next(context);
});

app.Run();
```

_Why this way?_ This `Program.cs` ties everything together. We register `HttpContextAccessor` and our custom `ITenantProvider`. We use `AddDbContext` to inject `AppDbContext` into the DI container, where it will resolve `ITenantProvider` and `ILogger`. The `MapGet` endpoint shows how simple data retrieval becomes – the developer doesn't need to _remember_ the tenant filter; it's already there. The `MapPost` demonstrates the importance of the `SaveChanges` override to ensure data integrity during writes. The `app.Use` middleware is a _simplistic stand-in for real authentication_. In a production system, this `ClaimsPrincipal` would be populated by your chosen authentication middleware (e.g., JWT bearer tokens, cookie authentication).

To test, you'd make requests like:
`GET /products` with `X-Tenant-Id: 4b1e4c7a-a63b-4e1a-8f6b-7c8d9e0f1a2b` to see Tenant A's products.
`GET /products` with `X-Tenant-Id: 2a3b4c5d-6e7f-8a9b-0c1d-2e3f4a5b6c7d` to see Tenant B's products.
Without the `X-Tenant-Id` header, the `HttpContextTenantProvider` would return `null`, and per our `OnModelCreating` logic, no filter would be applied (or potentially, queries would fail if you explicitly chose that policy). This highlights the critical dependency on robust authentication and tenant context resolution.

### Pitfalls, Trade-offs, and Best Practices

While Global Query Filters are a fantastic tool, they're not a magic bullet. Here's what I've learned from the trenches:

1.  **Writes are still your responsibility:** The filter applies to _reads_, not writes. As shown in the `SaveChanges` override, you _must_ explicitly stamp `TenantId` on new entities and validate it on updates. Failing to do so is a common and dangerous pitfall.
2.  **`IgnoreQueryFilters` is a loaded gun:** EF Core provides `IgnoreQueryFilters()` to bypass these filters. Use it sparingly, and only for specific administrative contexts where a "super user" genuinely needs to see data across all tenants. Audit its usage meticulously. A developer using this casually for debugging could accidentally ship a security vulnerability.
3.  **Performance and Indexing:** Global Query Filters add an `AND [TenantId] = @tenantId` clause to every query. If your `TenantId` column isn't properly indexed, this can lead to full table scans and significant performance degradation on large tables. _Always_ add an index to `TenantId`. Consider a composite index if you frequently filter by `TenantId` and another column.
4.  **Tenant Context Lifetime:** Ensure your `ITenantProvider` correctly resolves the tenant ID for the lifetime of the `DbContext`. In web applications, `Scoped` lifetime for `ITenantProvider` and `DbContext` works well. For background services, `AsyncLocal<T>` is often needed to explicitly flow the tenant ID across asynchronous operations, as `HttpContext` won't be available.
5.  **Handling Missing Tenant Context:** What happens if `GetCurrentTenantId()` returns null? My example logs a warning and proceeds without the filter (which is dangerous in production without further safeguards). A more secure approach might be to throw an `InvalidOperationException` in `OnModelCreating` if `currentTenantId` is null, preventing _any_ data access when the tenant context is ambiguous.
6.  **Base Entity Pattern:** For consistency and to reduce boilerplate, create a `TenantAwareEntity` base class or interface, and apply the filter dynamically as shown in `OnModelCreating`. This ensures new tenant-aware entities automatically inherit the filtering behavior.

### Conclusion

Building secure multi-tenant applications is a marathon, not a sprint. The lesson from that UAT scare stuck with me: robust security isn't about hoping developers remember every detail; it's about engineering systems that _enforce_ security by default. Entity Framework Core's Global Query Filters provide a powerful, production-ready mechanism to centralize tenant-based data isolation, pushing a critical security concern deep into the data access layer where it belongs. When combined with careful attention to write operations and disciplined usage of features like `IgnoreQueryFilters`, they offer a solid foundation for scalable, secure SaaS applications in the .NET ecosystem. Trust, but verify, and let your DAL do the heavy lifting of verification.
