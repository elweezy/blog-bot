---
layout: post
title: "Extending EF Core: Lifecycle Hooks, Named Filters, and Domain Events"
date: 2026-05-20 04:31:20 +0000
categories: dotnet blog
canonical_url: "https://dev.to/karenpayneoregon/ef-core-named-query-filters-54ej"
---

Navigating the complexities of data persistence in modern applications demands more than just basic CRUD operations. While Entity Framework Core provides an excellent abstraction over the database, building robust, maintainable, and scalable systems often requires extending its core capabilities. Specifically, integrating lifecycle hooks, leveraging named query filters, and adopting domain event patterns can transform a simple data layer into a sophisticated, business-rule-aware persistence mechanism.

The challenge frequently arises when business logic needs to intertwine with data operations in a consistent, non-intrusive way. Consider common requirements: auditing entity changes, implementing soft-deletes across various entities, or triggering subsequent actions (like notifications or cache invalidation) only after data has been successfully committed to the database. Without explicit extension points, developers often resort to scattering this logic across service layers, leading to duplication, potential inconsistencies, and a higher risk of introducing bugs. This is precisely where EF Core's extensibility model shines, allowing us to centralize and encapsulate these cross-cutting concerns.

### Beyond the Basics: EF Core's Extension Playbook

Modern .NET applications, especially those embracing microservices or cloud-native patterns, prioritize clear separation of concerns, transactional integrity, and responsiveness. EF Core's extension points empower architects to enforce these principles right at the data access boundary. By handling concerns like auditing or event dispatch within EF Core's lifecycle, we decouple them from core business logic, making our services cleaner, more testable, and easier to evolve. This approach isn't just about code organization; it's about reducing the cognitive load on developers and minimizing the chances of missing a critical step in a complex business transaction.

Let's break down three powerful mechanisms: lifecycle hooks (specifically interceptors), named query filters, and the integration of domain events.

#### Lifecycle Hooks: Intercepting Persistence Operations

EF Core's `SaveChangesInterceptor` is the primary entry point for injecting custom logic into the `SaveChanges` pipeline. Unlike the older `DbContext.SavingChanges` event, interceptors are strongly typed, fully asynchronous, and can be registered via Dependency Injection, making them a first-class citizen in modern .NET applications.

An interceptor can act *before* changes are sent to the database (`SavingChangesAsync` / `SavingChanges`) and *after* the database operation completes (`SavedChangesAsync` / `SavedChanges`). This distinction is critical. Operations that might prevent a save (e.g., validation) belong in `SavingChanges`. Actions that should only occur if the database transaction commits successfully (e.g., publishing domain events) are best placed in `SavedChangesAsync`. This guarantees transactional integrity – if the database commit fails, the post-save logic isn't triggered.

While `SaveChangesInterceptor` handles entity-level operations, `IDbCommandInterceptor` offers a lower-level hook into the database command execution itself. This is ideal for concerns like custom SQL logging, command retries, or even modifying command parameters before execution, without touching the entity graph. However, for most business-logic-driven scenarios, `SaveChangesInterceptor` is the more appropriate tool.

#### Named Query Filters: Consistent Data Access Rules

Global Query Filters, or what I often refer to as "named filters" because of their explicit definition, allow you to apply LINQ predicates to entity types directly within your `DbContext`'s `OnModelCreating`. These filters are automatically applied to *every* query involving that entity type, ensuring consistent application of rules like soft-deletion, multi-tenancy, or feature flagging.

```csharp
modelBuilder.Entity<Product>().HasQueryFilter(p => !p.IsDeleted);
```

This simple line ensures that any `dbContext.Products.Where(...)` or `dbContext.Products.Find(...)` call will silently exclude entities where `IsDeleted` is true. This saves immense boilerplate code and reduces the risk of developers forgetting to apply a crucial filter. The power lies in its ubiquity: it's enforced by EF Core itself.

The obvious trade-off is that these filters are *global*. If you genuinely need to retrieve soft-deleted entities, you must explicitly call `IgnoreQueryFilters()` on your query. This makes sense for administrative tools or specific archival processes but should be used sparingly in general application logic, as it bypasses the established rule. Over-reliance on `IgnoreQueryFilters()` can signal that the global filter might be too broad or that the application needs a more nuanced approach to data visibility.

#### Domain Events: Decoupling and Reactivity

Integrating domain events with EF Core persistence creates a robust mechanism for reacting to changes in your domain model. The pattern involves raising events from your aggregate roots when a significant business operation occurs (e.g., `ProductCreated`, `OrderShipped`). The crucial step is to ensure these events are dispatched *after* the changes have been successfully committed to the database. An `SaveChangesInterceptor` is the perfect place to orchestrate this.

By dispatching events post-commit, you maintain transactional consistency. If the `SaveChanges` operation fails, the events are never published, preventing invalid state transitions or erroneous downstream processing. Event handlers can then process these events, potentially asynchronously, leading to better system responsiveness and clearer separation of concerns.

### Practical Implementation: Auditing, Soft-Delete, and Event Dispatch

Let's consider a common scenario: we want to automatically track `CreatedOn` and `LastModifiedOn` timestamps, implement soft-deletion for certain entities, and dispatch domain events when significant entity changes occur.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations.Schema; // For ColumnType

// --- 1. Domain Interfaces and Base Entity ---

/// <summary>
/// Marks an entity as auditable, tracking creation and modification timestamps.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedOn { get; }
    DateTimeOffset? LastModifiedOn { get; }
    void SetCreatedOn(DateTimeOffset createdOn);
    void SetLastModifiedOn(DateTimeOffset? modifiedOn);
}

/// <summary>
/// Marks an entity for soft-deletion.
/// </summary>
public interface ISoftDelete
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedOn { get; }
    void SoftDelete(DateTimeOffset? deletedOn = null);
}

/// <summary>
/// Base interface for domain events.
/// </summary>
public interface IDomainEvent { DateTimeOffset OccurredOn { get; } }

/// <summary>
/// Base entity that supports domain events.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    private readonly List<IDomainEvent> _domainEvents = new();
    
    // Read-only collection of domain events
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    
    protected void AddDomainEvent(IDomainEvent eventItem) => _domainEvents.Add(eventItem);
    public void ClearDomainEvents() => _domainEvents.Clear();
}

// --- 2. Concrete Entity and Domain Events ---

public class Product : BaseEntity, ISoftDelete, IAuditable
{
    public string Name { get; private set; }
    public decimal Price { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedOn { get; private set; }

    public DateTimeOffset CreatedOn { get; private set; }
    public DateTimeOffset? LastModifiedOn { get; private set; }

    // Private constructor for EF Core or deserialization
    private Product() { }

    public Product(string name, decimal price)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Price = price;
        CreatedOn = DateTimeOffset.UtcNow; // Initial creation stamp
        AddDomainEvent(new ProductCreatedEvent(Id, Name, Price));
    }

    public void UpdateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Name cannot be empty.", nameof(newName));
        Name = newName;
        // LastModifiedOn will be set by interceptor
        AddDomainEvent(new ProductUpdatedEvent(Id, Name));
    }

    public void SoftDelete(DateTimeOffset? deletedOn = null)
    {
        if (IsDeleted) return; // Already deleted
        IsDeleted = true;
        DeletedOn = deletedOn ?? DateTimeOffset.UtcNow;
        // LastModifiedOn will be set by interceptor
        AddDomainEvent(new ProductDeletedEvent(Id));
    }
    
    // Explicit setters for interceptor to bypass private set
    public void SetCreatedOn(DateTimeOffset createdOn) => CreatedOn = createdOn;
    public void SetLastModifiedOn(DateTimeOffset? modifiedOn) => LastModifiedOn = modifiedOn;
}

public record ProductCreatedEvent(Guid ProductId, string Name, decimal Price) : IDomainEvent
{
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
public record ProductUpdatedEvent(Guid ProductId, string Name) : IDomainEvent
{
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
public record ProductDeletedEvent(Guid ProductId) : IDomainEvent
{
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}

// --- 3. Domain Event Dispatcher ---

/// <summary>
/// Interface for dispatching domain events. In a real app, this might use MediatR or a message bus.
/// </summary>
public interface IDomainEventDispatcher
{
    Task Dispatch(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>
/// Simple in-process dispatcher for demonstration.
/// </summary>
public class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly ILogger<DomainEventDispatcher> _logger;

    public DomainEventDispatcher(ILogger<DomainEventDispatcher> logger)
    {
        _logger = logger;
    }

    public async Task Dispatch(IEnumerable<IDomainEvent> events, CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            _logger.LogInformation("Dispatching domain event: {EventType} occurred at {OccurredOn}", 
                @event.GetType().Name, @event.OccurredOn);
            
            // Simulate async work for actual event handling (e.g., sending email, updating search index)
            await Task.Delay(10, cancellationToken); 
        }
    }
}

// --- 4. EF Core DbContext and Interceptor ---

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Global Query Filter for ISoftDelete entities
        modelBuilder.Entity<Product>().HasQueryFilter(p => !p.IsDeleted);

        // Configure Product properties
        modelBuilder.Entity<Product>().Property(p => p.Name).IsRequired().HasMaxLength(256);
        modelBuilder.Entity<Product>().Property(p => p.Price).HasColumnType("decimal(18,2)");
    }
}

/// <summary>
/// Interceptor to handle auditing timestamps and dispatch domain events.
/// </summary>
public class AuditingAndDomainEventInterceptor : SaveChangesInterceptor
{
    private readonly ILogger<AuditingAndDomainEventInterceptor> _logger;
    private readonly IDomainEventDispatcher _domainEventDispatcher;

    public AuditingAndDomainEventInterceptor(ILogger<AuditingAndDomainEventInterceptor> logger,
                                            IDomainEventDispatcher domainEventDispatcher)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _domainEventDispatcher = domainEventDispatcher ?? throw new ArgumentNullException(nameof(domainEventDispatcher));
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        HandleAuditing(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        HandleAuditing(eventData.Context);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void HandleAuditing(DbContext? context)
    {
        if (context == null) return;

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.SetCreatedOn(DateTimeOffset.UtcNow);
            }
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.SetLastModifiedOn(DateTimeOffset.UtcNow);
            }
        }
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        // Domain events are dispatched *after* the transaction has successfully committed.
        await DispatchDomainEventsAsync(eventData.Context, cancellationToken);
        return result;
    }

    private async ValueTask DispatchDomainEventsAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context == null) return;

        // Collect events from all entities that derive from BaseEntity
        var entitiesWithEvents = context.ChangeTracker.Entries<BaseEntity>()
            .Where(e => e.Entity.DomainEvents.Any())
            .Select(e => e.Entity)
            .ToList(); // Materialize to avoid 'collection modified' issues

        var domainEvents = entitiesWithEvents
            .SelectMany(e => e.DomainEvents)
            .OrderBy(e => e.OccurredOn) // Dispatch in order of occurrence
            .ToList();

        // Clear events from entities to ensure they are not dispatched again
        entitiesWithEvents.ForEach(e => e.ClearDomainEvents());

        if (domainEvents.Any())
        {
            _logger.LogInformation("Found {Count} domain events to dispatch after save.", domainEvents.Count);
            try
            {
                await _domainEventDispatcher.Dispatch(domainEvents, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch domain events after SaveChanges. Consider a robust retry mechanism.");
                // In a production system, you might enqueue these events to a durable queue (e.g., Azure Service Bus, RabbitMQ)
                // for guaranteed delivery and processing, rather than re-throwing here,
                // as the database commit has already succeeded.
                throw; // Re-throw for immediate feedback/circuit breaking in this example.
            }
        }
    }
}

// --- 5. Application Setup (Minimal API Example) ---

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // Add services to the container.
        builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        // Interceptors are often registered as singletons if they are stateless, 
        // or scoped if they have state tied to the DbContext's lifetime.
        // This interceptor is stateless with dependencies injected, so singleton is fine.
        builder.Services.AddSingleton<AuditingAndDomainEventInterceptor>(); 

        builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            options.UseInMemoryDatabase("ProductDb"); // Use an in-memory database for simplicity
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditingAndDomainEventInterceptor>());
        });

        // Minimal API related services
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        // Minimal API endpoints
        app.MapPost("/products", async (ProductDto productDto, AppDbContext dbContext, CancellationToken ct) =>
        {
            var product = new Product(productDto.Name, productDto.Price);
            await dbContext.Products.AddAsync(product, ct);
            await dbContext.SaveChangesAsync(ct); // Interceptor handles auditing and event dispatch
            return Results.Created($"/products/{product.Id}", product);
        })
        .WithName("CreateProduct")
        .WithOpenApi();

        app.MapGet("/products", async (AppDbContext dbContext, CancellationToken ct) =>
        {
            // This query automatically applies the soft-delete filter
            return Results.Ok(await dbContext.Products.ToListAsync(ct));
        })
        .WithName("GetProducts")
        .WithOpenApi();

        app.MapGet("/products/{id}", async (Guid id, AppDbContext dbContext, CancellationToken ct) =>
        {
            // Will only return if not soft-deleted due to global filter
            var product = await dbContext.Products.FindAsync(new object[] { id }, ct);
            if (product == null)
            {
                return Results.NotFound($"Product with ID {id} not found or is soft-deleted.");
            }
            return Results.Ok(product);
        })
        .WithName("GetProductById")
        .WithOpenApi();
        
        // Example to retrieve all products including soft-deleted ones (admin view)
        app.MapGet("/products/all", async (AppDbContext dbContext, CancellationToken ct) =>
        {
            return Results.Ok(await dbContext.Products.IgnoreQueryFilters().ToListAsync(ct));
        })
        .WithName("GetAllProductsIncludingDeleted")
        .WithOpenApi();


        app.MapDelete("/products/{id}", async (Guid id, AppDbContext dbContext, CancellationToken ct) =>
        {
            // Retrieve product (respecting global filter initially, though we'll find it by ID)
            var product = await dbContext.Products.FindAsync(new object[] { id }, ct);
            if (product == null)
            {
                return Results.NotFound();
            }
            
            product.SoftDelete(); // Marks for soft-deletion and adds domain event
            dbContext.Products.Update(product); // Mark entity as modified
            await dbContext.SaveChangesAsync(ct); // Interceptor handles auditing and event dispatch
            
            return Results.NoContent();
        })
        .WithName("SoftDeleteProduct")
        .WithOpenApi();

        await app.RunAsync();
    }
}

public record ProductDto(string Name, decimal Price);
```

In this example:
*   **`IAuditable`** and **`ISoftDelete`** interfaces define contract for auditing and soft-deletion.
*   **`BaseEntity`** encapsulates common properties like `Id` and manages the collection of `IDomainEvent`s.
*   The **`Product`** entity implements these interfaces and adds domain events when its state changes (creation, update, soft-delete).
*   **`AppDbContext`** defines the `Product` `DbSet` and crucial **`HasQueryFilter`** for soft-deletion. This filter ensures that `IsDeleted` products are automatically excluded from queries, promoting consistency across the application.
*   **`AuditingAndDomainEventInterceptor`**, inheriting from `SaveChangesInterceptor`, is the powerhouse.
    *   It implements `SavingChangesAsync` to automatically set `CreatedOn` for new entities and `LastModifiedOn` for modified entities that implement `IAuditable`.
    *   It implements `SavedChangesAsync` to reliably dispatch collected domain events *after* the `SaveChanges` operation completes successfully. This ensures that downstream systems only react to committed data.
*   **`IDomainEventDispatcher`** abstracts the actual event handling. The `DomainEventDispatcher` implementation logs the events and simulates async work, but in a production scenario, this would typically involve a message broker (e.g., RabbitMQ, Azure Service Bus) or an in-process dispatcher like MediatR to fan out events to various handlers, potentially in different services or background workers.
*   The **`Program.cs`** demonstrates how to register the interceptor and other services using Dependency Injection, and how minimal API endpoints leverage the `DbContext` and its extended capabilities. Notice the `IgnoreQueryFilters()` call in the `/products/all` endpoint, showcasing how to deliberately bypass the global filter when required.

This setup centralizes auditing, soft-delete logic, and domain event dispatch, removing this boilerplate from individual service methods.

### Pitfalls and Best Practices

While these extension patterns offer immense power, they come with their own set of considerations:

*   **Over-interception:** Too many interceptors, or complex logic within them, can obscure the flow of execution and make debugging challenging. Reserve interceptors for truly cross-cutting concerns that apply consistently across many entities or operations.
*   **Performance Impact:** Complex logic in `SavingChanges` can impact the overall transaction time. Be mindful of synchronous blocking operations. For `SavedChanges`, remember that any errors in event dispatch will occur *after* the database commit, necessitating robust error handling, retries, or dead-letter queues for events.
*   **Global Query Filter Granularity:** While convenient, global query filters can sometimes be *too* global. If a significant portion of your application requires `IgnoreQueryFilters()`, it might indicate that the "global" rule isn't as universal as initially thought, and a more explicit filtering strategy might be better. Always document your global filters clearly.
*   **Domain Event Transactionality:** Dispatching events *after* `SaveChanges` is crucial for transactional integrity. However, it also means the event dispatch itself isn't part of the database transaction. If event dispatch fails (e.g., message broker is down), the database state is committed but the event might be lost. This highlights the need for a robust event publishing and consumption mechanism, often involving outbox patterns or reliable messaging systems for eventual consistency.
*   **Testing:** Interceptors and global filters can sometimes make unit testing harder as they're implicitly applied. Design your tests to account for these behaviors or provide mechanisms to temporarily disable them during specific tests if necessary. Mocking `DbContext` and its interceptors can be complex, often pushing towards integration tests for these layers.

Ultimately, these patterns are tools. Their effective application hinges on understanding their strengths, weaknesses, and the specific requirements of your domain.

### A More Resilient Data Layer

Extending EF Core with lifecycle hooks, named query filters, and domain events isn't just about adding features; it's about engineering a more robust, maintainable, and predictable data access layer. By centralizing cross-cutting concerns, we reduce boilerplate, improve consistency, and free our core business logic to focus on what truly matters. This disciplined approach ensures that our data layer is not just a mechanism for storing and retrieving bytes, but an active participant in enforcing business rules and driving the overall system's behavior, adapting gracefully to the demands of modern cloud-native architectures.
