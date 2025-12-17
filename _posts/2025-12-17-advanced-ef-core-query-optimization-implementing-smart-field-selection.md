---
layout: post
title: "Advanced EF Core Query Optimization: Implementing Smart Field Selection"
date: 2025-12-17 03:33:27 +0000
categories: dotnet blog
canonical_url: "https://dev.to/mohammad_aliebrahimzadeh/introducing-keplercore-smart-field-selection-for-ef-core-apis-4da2"
---

A common scenario: you're profiling a new API endpoint, perhaps one designed to return a list of `Product` objects for a dashboard widget. The database query itself, when isolated, seems performant enough. Yet, the overall API response time is sluggish, and network transfer metrics are unexpectedly high. Digging deeper reveals the culprit: you're shipping `Product` entities that contain `DescriptionHtml`, `InternalNotes`, `SupplierContractDetails`, and a dozen other heavyweight fields that the client consuming this particular endpoint simply doesn't need for its summary view. The `SELECT *` mentality, even if implicit through `ToListAsync()` on an `IQueryable<TEntity>`, is a silent performance killer that scales poorly with data volume and user load.

In the realm of modern cloud-native applications, where every byte transmitted, every millisecond spent, and every kilobyte of memory consumed has a tangible cost, optimizing data transfer is paramount. Unnecessary data flowing from the database to the application layer, and then out to the client, impacts network bandwidth, server memory, CPU cycles for serialization/deserialization, and ultimately, user experience. Entity Framework Core, while a magnificent ORM for developer productivity, can inadvertently facilitate this over-fetching if not used with care and precision. This isn't just about database performance; it's about holistic system efficiency.

The first line of defense against over-fetching in EF Core is static projection to Data Transfer Objects (DTOs). Instead of fetching `DbSet<Product>.ToListAsync()`, we explicitly shape the result:

```csharp
public class ProductSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
}

// ... inside a service or repository ...
public async Task<IEnumerable<ProductSummaryDto>> GetProductSummariesAsync(
    ApplicationDbContext dbContext,
    CancellationToken cancellationToken)
{
    return await dbContext.Products
        .AsNoTracking() // Essential for read-only queries
        .Select(p => new ProductSummaryDto
        {
            Id = p.Id,
            Name = p.Name,
            Price = p.Price,
            IsActive = p.IsActive
        })
        .ToListAsync(cancellationToken);
}
```

This pattern is effective because EF Core's query translator analyzes the `Select` expression and generates SQL that only retrieves the columns explicitly mapped to `ProductSummaryDto` properties. This avoids fetching entire rows from the database, minimizing I/O and network traffic between your application and the database server. The `AsNoTracking()` call is equally critical for read-only scenarios, preventing EF Core from attaching entities to the change tracker, which saves memory and CPU overhead.

However, the static DTO approach quickly encounters limitations in dynamic scenarios. What if different client applications or even different parts of the same application require varying subsets of fields from the `Product` entity? Creating a new DTO for every permutation (`ProductSummaryDto`, `ProductDetailsDto`, `ProductWithInventoryDto`) leads to DTO proliferation and maintenance overhead. This is where dynamic field selection, often implemented using C# Expression Trees, becomes invaluable.

The core idea is to programmatically construct the `Expression<Func<TEntity, TResult>>` used in the `.Select()` method at runtime, based on a client's request. This allows us to generate highly optimized queries on the fly, fetching precisely what's needed.

Let's walk through a simplified example of how we might implement this within a minimal API.

First, define a typical entity and a base DTO. We'll use reflection and expression trees to build projections to this base DTO, or even anonymous types, selecting only the requested properties from the entity.

```csharp
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --- Entities and DTOs ---
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty; // Often long
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsActive { get; set; }
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!; // Navigation property
}

public class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<Product> Products { get; set; } = new List<Product>();
}

// A base DTO, though dynamic projection often creates anonymous types
// or specifically shaped DTOs on the fly.
public class ProductLightDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // Other properties will be added dynamically if requested
}

// --- DbContext ---
public class ApplicationDbContext : DbContext
{
    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId);
    }
}

// --- Dynamic Projection Service ---
public class DynamicProjectionService
{
    private readonly ILogger<DynamicProjectionService> _logger;

    public DynamicProjectionService(ILogger<DynamicProjectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a dynamic projection expression for a given entity type
    /// based on a list of requested property names.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>An Expression<Func<TEntity, object>> that selects the requested properties.</returns>
    public Expression<Func<TEntity, object>> CreateProjectionExpression<TEntity>(
        IEnumerable<string> requestedFields)
    {
        var entityType = typeof(TEntity);
        var parameter = Expression.Parameter(entityType, "e");

        // Use a Dictionary to construct dynamic properties. This will project to an anonymous type.
        var propertyBindings = new List<MemberBinding>();
        var propertiesToSelect = new HashSet<string>(requestedFields, StringComparer.OrdinalIgnoreCase);

        // Always include Id for consistency or if it's a primary key needed for client-side operations.
        if (!propertiesToSelect.Contains("Id") && entityType.GetProperty("Id") != null)
        {
            propertiesToSelect.Add("Id");
        }

        foreach (var fieldName in propertiesToSelect)
        {
            var entityProperty = entityType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (entityProperty == null)
            {
                _logger.LogWarning("Requested field '{FieldName}' not found on entity '{EntityType}'.", fieldName, entityType.Name);
                continue;
            }

            // Create a MemberAssignment (e.g., Id = e.Id)
            var memberAccess = Expression.Property(parameter, entityProperty);
            propertyBindings.Add(Expression.Bind(entityProperty, memberAccess));
        }

        // Handle the case where no valid fields were requested or found
        if (!propertyBindings.Any())
        {
            // Fallback: project just the Id if available, or return a default empty object
            var idProperty = entityType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            if (idProperty != null)
            {
                propertyBindings.Add(Expression.Bind(idProperty, Expression.Property(parameter, idProperty)));
            }
            else
            {
                // If TEntity has no 'Id' and no fields requested, project a new empty object.
                // This scenario might indicate an issue with field selection logic.
                _logger.LogWarning("No valid fields found for dynamic projection for entity '{EntityType}'. Returning an empty object projection.", entityType.Name);
                return Expression.Lambda<Func<TEntity, object>>(Expression.New(typeof(object)), parameter);
            }
        }

        // Create an anonymous type dynamically
        // This is a common pattern for dynamic projections in EF Core.
        // The `MemberInit` creates an object and initializes its members.
        var newExpression = Expression.New(typeof(object)); // Placeholder, EF Core will adapt
        var memberInit = Expression.MemberInit(newExpression, propertyBindings);

        return Expression.Lambda<Func<TEntity, object>>(memberInit, parameter);
    }
}


// --- Minimal API Endpoint ---
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase("ProductDb")); // Using in-memory for simplicity
        builder.Services.AddScoped<DynamicProjectionService>();
        builder.Services.AddLogging(); // Ensure logging is configured

        var app = builder.Build();

        // Seed data (for in-memory DB)
        using (var scope = app.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await SeedData(dbContext);
        }

        // Configure the HTTP request pipeline.
        app.UseHttpsRedirection();

        app.MapGet("/products", async (
            HttpContext context,
            ApplicationDbContext dbContext,
            DynamicProjectionService projectionService,
            ILogger<Program> logger) =>
        {
            // Example: Client requests fields via query parameter "fields=Id,Name,Price"
            var requestedFields = context.Request.Query["fields"].ToString()
                                 ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .ToArray();

            // Default fields if none are specified
            if (requestedFields == null || !requestedFields.Any())
            {
                requestedFields = new[] { "Id", "Name", "Price", "Sku", "IsActive" }; // Reasonable default
            }

            logger.LogInformation("Processing /products request with fields: {Fields}", string.Join(", ", requestedFields));

            try
            {
                // Create the dynamic projection expression
                var projection = projectionService.CreateProjectionExpression<Product>(requestedFields);

                // Apply the projection and execute the query
                var products = await dbContext.Products
                    .AsNoTracking()
                    .Select(projection) // Apply the dynamic projection
                    .ToListAsync();

                // Because we project to 'object' (an anonymous type internally),
                // we need to handle serialization carefully.
                // The anonymous type has properties matching the requested fields.
                // ASP.NET Core's JSON serializer (System.Text.Json) handles anonymous types well.
                return Results.Ok(products);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching products with dynamic projection.");
                return Results.Problem("An error occurred while fetching products.", statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("GetProductsWithDynamicProjection")
        .WithOpenApi(); // For Swagger/OpenAPI support

        app.Run();
    }

    private static async Task SeedData(ApplicationDbContext dbContext)
    {
        if (!await dbContext.Products.AnyAsync())
        {
            var category1 = new Category { Id = Guid.NewGuid(), Name = "Electronics" };
            var category2 = new Category { Id = Guid.NewGuid(), Name = "Books" };

            await dbContext.Categories.AddRangeAsync(category1, category2);

            await dbContext.Products.AddRangeAsync(
                new Product { Id = Guid.NewGuid(), Name = "Laptop Pro", Sku = "LP-1001", Description = "High-performance laptop.", Price = 1200.00m, StockQuantity = 50, CreatedAt = DateTime.UtcNow, IsActive = true, CategoryId = category1.Id, Category = category1 },
                new Product { Id = Guid.NewGuid(), Name = "Mechanical Keyboard", Sku = "MK-2002", Description = "Tactile mechanical keyboard.", Price = 150.00m, StockQuantity = 100, CreatedAt = DateTime.UtcNow, IsActive = true, CategoryId = category1.Id, Category = category1 },
                new Product { Id = Guid.NewGuid(), Name = "The Hitchhiker's Guide to the Galaxy", Sku = "BK-3001", Description = "A classic science fiction novel.", Price = 12.99m, StockQuantity = 200, CreatedAt = DateTime.UtcNow, IsActive = true, CategoryId = category2.Id, Category = category2 },
                new Product { Id = Guid.NewGuid(), Name = "Clean Code", Sku = "BK-3002", Description = "A handbook of agile software craftsmanship.", Price = 45.00m, StockQuantity = 75, CreatedAt = DateTime.UtcNow, IsActive = true, CategoryId = category2.Id, Category = category2 },
                new Product { Id = Guid.NewGuid(), Name = "Product X (Inactive)", Sku = "PX-9999", Description = "An inactive product for testing.", Price = 99.99m, StockQuantity = 0, CreatedAt = DateTime.UtcNow, IsActive = false, CategoryId = category1.Id, Category = category1 }
            );
            await dbContext.SaveChangesAsync();
        }
    }
}
```

This example leverages:
*   **Minimal APIs**: A concise way to define API endpoints in modern .NET.
*   **Dependency Injection**: `ApplicationDbContext` and `DynamicProjectionService` are injected, demonstrating production-level patterns.
*   **Logging**: `ILogger` is used to provide insight into requested fields and potential issues, crucial for debugging and monitoring.
*   **Expression Trees (`System.Linq.Expressions`)**: The `CreateProjectionExpression` method is the heart of the dynamic selection. It constructs an `Expression<Func<TEntity, object>>` which represents the `.Select()` clause. `Expression.Parameter` defines the input variable (`e` in `e => ...`), `Expression.Property` accesses entity properties (e.g., `e.Id`), and `Expression.MemberInit` creates a new object (often an anonymous type, which EF Core's translator handles beautifully) and initializes its members with the selected properties.
*   **`AsNoTracking()`**: Ensures that read-only queries don't incur change tracking overhead.

When a request like `/products?fields=Name,Price` hits the endpoint, the `DynamicProjectionService` builds an expression equivalent to `p => new { Name = p.Name, Price = p.Price }`. EF Core translates this into a SQL `SELECT` statement that *only* includes the `Name` and `Price` columns, dramatically reducing the database's workload and the network payload.

### Pitfalls & Best Practices

1.  **Overhead of Expression Building**: Building expressions dynamically involves reflection and expression tree manipulation, which has some CPU overhead. For highly frequent calls with static field requirements, a pre-compiled or cached static DTO projection is still faster. Dynamic projection shines when field requirements truly vary per request or client. Consider caching generated expressions if the set of `requestedFields` is finite and repetitive.
2.  **Navigation Properties**: My example focuses on direct properties. Dynamically projecting nested navigation properties (e.g., `Category.Name`) adds significant complexity to the expression tree building. It requires creating nested `MemberInit` expressions and potentially handling `Include()` statements or join operations. For deeply nested structures, GraphQL-like solutions or specialized libraries might be more appropriate.
3.  **Security and Validation**: Accepting arbitrary field names from client requests can be a security risk if not properly validated. Malicious users could try to infer internal property names or cause exceptions. Always validate `requestedFields` against a whitelist of allowed public properties. My example includes basic logging for unknown fields, but a robust solution would deny invalid requests or silently ignore unknown fields.
4.  **Serialization**: When projecting to `object` or anonymous types, ensure your serializer (like `System.Text.Json` in ASP.NET Core) can handle them correctly. Generally, it does, producing JSON based on the dynamically created type's properties.
5.  **Not a Replacement for `AsNoTracking()`**: Dynamic projection optimizes the *columns* selected, while `AsNoTracking()` optimizes the *tracking* overhead. Both are crucial for performance in read-only scenarios.
6.  **`SELECT N+1` Problem**: Dynamic projection helps with *horizontal* over-fetching (too many columns). It does not directly solve the *vertical* over-fetching (N+1 queries) that arises from lazy loading navigation properties. For that, explicit `.Include()` or further `.Select()` projections are necessary.

Implementing smart field selection using expression trees is a powerful technique for fine-tuning EF Core queries, aligning your data fetching precisely with client needs. It's a testament to the flexibility of .NET's language features and EF Core's robust query translation engine. While it introduces a layer of complexity in implementation, the reduction in database load and API payload sizes for data-intensive services can be substantial, leading to more performant, scalable, and cost-efficient applications. Ultimately, mastering such advanced patterns is about understanding the underlying mechanisms and applying them judiciously where their benefits outweigh their engineering cost.
