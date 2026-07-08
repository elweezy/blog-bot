---
layout: post
title: "Optimizing Database Pagination in .NET with Keyset Pagination"
date: 2026-07-08 04:06:12 +0000
categories: dotnet blog
canonical_url: "https://dev.to/lttruc1402/why-skiptake-gets-slower-on-every-page-and-how-keyset-pagination-fixes-it-2jmn"
---

The performance characteristics of data retrieval operations often reveal themselves only after a system has been running in production for some time, accumulating significant data volumes. A classic scenario I've encountered repeatedly involves web APIs backing large tabular displays, where initial page loads are snappy, but as users scroll or navigate deeper into the data set, response times spiral, sometimes into many seconds. This isn't just an inconvenience; it can cripple a user experience and consume disproportionate database resources, leading to cascading performance issues across an application.

The root cause in these situations is almost invariably the naive application of `OFFSET` and `FETCH NEXT` (or `SKIP` and `TAKE` in LINQ) for database pagination. While perfectly acceptable for small datasets or the first few pages, this approach scales poorly, turning into a significant bottleneck as the offset grows.

### The Inefficiency of `OFFSET`/`FETCH`

Consider a typical `OFFSET` query:

```sql
SELECT Id, Name, CreatedDate, ...
FROM Products
ORDER BY CreatedDate DESC, Id ASC
OFFSET 100000 ROWS
FETCH NEXT 50 ROWS;
```

To fulfill this request, the database engine must logically sort *all* relevant rows up to the `OFFSET` value, then discard them, only returning the `FETCH NEXT` set. Even with appropriate indexing on `CreatedDate` and `Id`, the database often still performs a substantial amount of work to reach the starting point. This work increases linearly with the `OFFSET` value, leading to `O(N)` complexity where N is the offset. On systems with millions of records and users trying to access page 2000, this translates directly to unacceptable query times and high CPU utilization on the database server.

In the era of cloud-native applications, microservices, and ever-growing datasets, such inefficiencies are simply unsustainable. Modern front-end experiences often favor infinite scrolling or continuous data loading, making consistent, fast retrieval of subsequent data chunks paramount. This demand highlights a critical need for a more performant pagination strategy: **keyset pagination**.

### Keyset Pagination: Seeking Data, Not Skipping It

Keyset pagination, also known as "cursor-based" or "seek" pagination, fundamentally alters how the database retrieves the "next page" of data. Instead of telling the database to skip a certain number of rows, we tell it where we *left off* in the previous request. This "cursor" is typically derived from the last item returned in the prior page.

The core idea is to leverage indexed columns directly in a `WHERE` clause to efficiently seek to the next set of records. This transforms the operation from an `O(N)` scan to an `O(log N)` index seek, making it vastly more scalable.

Let's assume our `Products` table has `Id` (primary key, `Guid`), `Name` (string), and `CreatedDate` (`DateTimeOffset`). If we're sorting by `CreatedDate` descending, and then `Id` ascending for tie-breaking, a keyset query for the *next* 50 items might look like this:

```sql
SELECT Id, Name, CreatedDate, ...
FROM Products
WHERE
    (CreatedDate < @LastCreatedDate) OR
    (CreatedDate = @LastCreatedDate AND Id > @LastId)
ORDER BY CreatedDate DESC, Id ASC
FETCH NEXT 50 ROWS;
```

Here, `@LastCreatedDate` and `@LastId` are the values from the *last* item returned in the previous page. The query effectively asks for "all products created after `@LastCreatedDate` (or on the same date but with a greater `Id`)". This immediately jumps to the relevant section of the index, avoiding the full scan of preceding rows.

### Implementing Keyset Pagination in .NET with Entity Framework Core

Integrating keyset pagination into a modern .NET application, especially one leveraging Entity Framework Core, is straightforward but requires careful construction of the query predicates. We'll demonstrate this using a Minimal API endpoint, a dedicated service, and `IAsyncEnumerable` for efficient data streaming.

First, define a `Product` entity and a context:

```csharp
// Product.cs
public record Product(Guid Id, string Name, DateTimeOffset CreatedDate, decimal Price);

// ProductContext.cs
public class ProductContext : DbContext
{
    public ProductContext(DbContextOptions<ProductContext> options) : base(options) { }
    public DbSet<Product> Products { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().HasKey(p => p.Id);
        // Ensure an index on the sorting columns for performance
        modelBuilder.Entity<Product>().HasIndex(p => new { p.CreatedDate, p.Id });
        // Seed some data for demonstration
        modelBuilder.Entity<Product>().HasData(
            new Product(Guid.Parse("a1b2c3d4-e5f6-7890-1234-567890abcdef"), "Laptop", DateTimeOffset.UtcNow.AddDays(-10), 1200m),
            new Product(Guid.Parse("b1c2d3e4-f5a6-8901-2345-67890abcdef0"), "Mouse", DateTimeOffset.UtcNow.AddDays(-9), 25m),
            new Product(Guid.Parse("c1d2e3f4-a5b6-9012-3456-7890abcdef01"), "Keyboard", DateTimeOffset.UtcNow.AddDays(-8), 75m),
            new Product(Guid.Parse("d1e2f3a4-b5c6-0123-4567-890abcdef012"), "Monitor", DateTimeOffset.UtcNow.AddDays(-7), 300m),
            new Product(Guid.Parse("e1f2a3b4-c5d6-1234-5678-90abcdef0123"), "Webcam", DateTimeOffset.UtcNow.AddDays(-6), 50m)
            // ... imagine thousands more
        );
    }
}
```

Next, a service to encapsulate the pagination logic. This service takes `lastCreatedDate` and `lastId` as "cursor" parameters.

```csharp
// ProductService.cs
public class ProductService
{
    private readonly ProductContext _context;
    private readonly ILogger<ProductService> _logger;

    public ProductService(ProductContext context, ILogger<ProductService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async IAsyncEnumerable<Product> GetProductsPagedAsync(
        int pageSize,
        DateTimeOffset? lastCreatedDate = null,
        Guid? lastId = null)
    {
        IQueryable<Product> query = _context.Products;

        // Apply keyset pagination logic
        if (lastCreatedDate.HasValue && lastId.HasValue)
        {
            _logger.LogInformation("Applying keyset pagination: LastCreatedDate={@lastCreatedDate}, LastId={@lastId}", lastCreatedDate, lastId);
            query = query.Where(p =>
                p.CreatedDate < lastCreatedDate.Value ||
                (p.CreatedDate == lastCreatedDate.Value && p.Id > lastId.Value));
        }
        else
        {
            _logger.LogInformation("No keyset provided, fetching first page.");
        }

        query = query
            .OrderByDescending(p => p.CreatedDate)
            .ThenBy(p => p.Id)
            .Take(pageSize);

        // Stream results using IAsyncEnumerable
        await foreach (var product in query.AsAsyncEnumerable())
        {
            yield return product;
        }
    }
}
```

Finally, the Minimal API setup in `Program.cs`.

```csharp
// Program.cs
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization; // For JsonStringEnumConverter
using KeysetPaginationDemo; // Namespace for Product, ProductContext, ProductService

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure DbContext with an in-memory database for demo purposes
// In production, this would be a real SQL database connection string.
builder.Services.AddDbContext<ProductContext>(options =>
    options.UseInMemoryDatabase("ProductsDb")); // For demo, use in-memory

builder.Services.AddScoped<ProductService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // Seed data on startup for the in-memory database
    using (var scope = app.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<ProductContext>();
        context.Database.EnsureCreated(); // Creates the in-memory database
        // Data seeding is already handled in OnModelCreating,
        // but for a real DB, you might run migrations or a separate seeding logic here.
    }
}

app.UseHttpsRedirection();

// Minimal API endpoint for keyset pagination
app.MapGet("/products/paged", async (
    [FromServices] ProductService productService,
    [FromQuery] int pageSize = 10,
    [FromQuery] DateTimeOffset? lastCreatedDate = null,
    [FromQuery] Guid? lastId = null) =>
{
    // Validate inputs
    if (pageSize <= 0 || pageSize > 100)
    {
        return Results.BadRequest("Page size must be between 1 and 100.");
    }

    var products = productService.GetProductsPagedAsync(pageSize, lastCreatedDate, lastId);

    // To return the cursor for the next page, we need to consume the results
    // and extract the last item's keys.
    // For simplicity in this demo, we'll collect all products.
    // In a real-world scenario, you might return the products and the cursor separately
    // perhaps in a custom response object, or rely on the client to extract the cursor.
    var productList = await products.ToListAsync();

    // Prepare the cursor for the next request
    var nextCursor = productList.LastOrDefault() != null
        ? new { lastCreatedDate = productList.Last().CreatedDate, lastId = productList.Last().Id }
        : null;

    return Results.Ok(new { Products = productList, NextCursor = nextCursor });
})
.WithName("GetProductsPaged")
.WithOpenApi();

app.Run();

```

This example leverages:
*   **Dependency Injection** for `ProductService` and `ProductContext`.
*   **Minimal APIs** for a concise endpoint definition.
*   **`IAsyncEnumerable<T>`**: The `GetProductsPagedAsync` method returns `IAsyncEnumerable<Product>`. This is crucial for performance and scalability, as it allows results to be streamed to the client as they are retrieved from the database, rather than buffering the entire page in memory. It's particularly effective when paired with clients that can process streamed data.
*   **Structured Logging**: `ILogger` is injected and used to provide context about the pagination execution.
*   **EF Core Query Construction**: The `Where` clause correctly implements the keyset logic using a composite condition. `OrderByDescending().ThenBy()` ensures consistent ordering, critical for keyset pagination.

The client would make an initial request to `/products/paged?pageSize=10`. After receiving the first page, it would extract `lastCreatedDate` and `lastId` from the *last product in the returned list* (or the `NextCursor` object provided in the response) and use these values for the next request: `/products/paged?pageSize=10&lastCreatedDate=...&lastId=...`.

### Pitfalls and Best Practices

While keyset pagination is powerful, it's not a silver bullet and comes with its own set of considerations:

1.  **Choosing the Right Keys**:
    *   **Uniqueness**: The combination of keys in your cursor (`CreatedDate`, `Id` in our example) must uniquely identify each record. If `CreatedDate` alone isn't unique, you need a tie-breaker like `Id`.
    *   **Immutability**: The keys used for pagination should ideally be immutable. If `CreatedDate` could change, it could break the pagination sequence. `Id` is usually a safe bet.
    *   **Indexing**: The chosen keys must be part of an efficient index. A composite index `(CreatedDate DESC, Id ASC)` is essential for the query to leverage it optimally.
2.  **No "Go to Page X"**: A direct consequence of keyset pagination is that users cannot arbitrarily jump to page 5 or 10. Navigation is typically "next" or "previous." This is well-suited for infinite scroll UIs but not for traditional numbered pagers. If "go to page X" is a strict requirement, you might need to combine keyset pagination with an estimated page count (which can be expensive) or offer a hybrid approach for the first few pages.
3.  **Handling Deleted/Modified Records**: If records are deleted or their sort keys change between requests, the pagination sequence might skip some records or show duplicates. For most applications, this is an acceptable trade-off for performance, especially when dealing with large, frequently updated datasets where exact page counts are less critical than continuous flow.
4.  **Combining with Filtering and Sorting**: Keyset pagination combines well with additional `WHERE` clauses for filtering. Just ensure the `ORDER BY` clause still starts with the keyset columns to maintain index usage. For dynamic sorting, the keyset logic must adapt to the new sort order and potentially use different key columns.
5.  **Performance Trade-offs**: While `OFFSET`/`FETCH` degrades linearly, keyset pagination offers consistent `O(log N)` performance regardless of how deep you go. The trade-off is often a slightly more complex client-side implementation for tracking the cursor.
6.  **`IAsyncEnumerable` for Streaming**: The use of `IAsyncEnumerable` is a modern .NET pattern that complements keyset pagination beautifully. It enables efficient streaming of results, reducing server memory footprint and improving perceived latency for the client. The client can then process items as they arrive, which is ideal for scenarios like real-time dashboards or large data exports.

### Conclusion

For systems grappling with large datasets and performance degradation on deeper pagination pages, keyset pagination offers a robust and scalable solution. By shifting from an offset-based "skip N rows" paradigm to a cursor-based "find where I left off" approach, we empower the database to leverage its indexes efficiently, delivering consistent and fast data retrieval. Understanding its implications and trade-offs, particularly around user navigation and cursor management, is key to successfully integrating it into modern .NET applications. It's a pattern that has proven its worth in high-scale production environments and remains a vital tool in an architect's toolkit for building truly performant data-driven systems.
