---
layout: post
title: "Building High-Performance Distributed Caching Solutions in .NET"
date: 2026-06-10 04:35:09 +0000
categories: dotnet blog
canonical_url: "https://dev.to/__2d3e61e/why-squirix-uses-a-strict-clientserver-architecture-for-a-net-distributed-cache-5086"
---

A few years back, we were battling a particularly stubborn performance bottleneck in a critical service. Our backend, a collection of ASP.NET Core microservices, was hitting a SQL Server instance with a high volume of read queries for relatively static configuration data and product catalogs. The database wasn't failing, but response times were creeping up, and scaling the database horizontally was becoming prohibitively expensive and complex. The obvious solution was caching. But "caching" in a distributed, high-traffic environment is rarely just a matter of slapping `MemoryCache.GetOrCreate` everywhere. It's an architectural decision with significant implications for performance, consistency, and resilience.

This isn't about *if* you should cache; it's about *how* to build high-performance distributed caching solutions in .NET that truly scale and perform under pressure. Modern cloud-native architectures, with their emphasis on microservices, stateless application instances, and horizontal scaling, necessitate a robust distributed caching layer. In-memory caching, while excellent for single-instance performance, becomes a liability when instances scale out or restart, leading to cache misses and a "thundering herd" problem on the backend data source.

### The Landscape of Distributed Caching in .NET

The .NET ecosystem provides excellent primitives for building distributed caching, primarily through the `IDistributedCache` interface in `Microsoft.Extensions.Caching.Distributed`. This abstraction allows you to swap out caching providers (Redis, SQL Server, NCache, etc.) with minimal code changes. However, `IDistributedCache` is a relatively high-level abstraction. For true high-performance scenarios, understanding the underlying cache store and its specific features is paramount. Most often, this leads us to Redis.

Redis, with its in-memory data structures, impressive speed, and rich feature set (pub/sub, streams, Lua scripting, atomic operations), has become the de-facto standard for distributed caching, message brokering, and session management in modern applications. Leveraging Redis effectively from .NET requires thoughtful design.

The core architectural pattern for most distributed caches is "cache-aside." The application tries to read data from the cache. If it's a miss, it fetches the data from the primary source (e.g., database), stores it in the cache, and then returns it. On a write, the application typically writes to the primary data source first, then invalidates or updates the cache. This maintains a clear source of truth while offloading read pressure.

### Key Considerations for Performance

When designing your distributed cache, several factors directly impact performance:

1.  **Network Latency**: This is often the biggest bottleneck. Every cache interaction is a network round trip. Minimize chatty interactions by:
    *   **Batching**: Use Redis's `MGET` or pipelining to retrieve multiple items in a single request.
    *   **Layered Caching**: A small, fast `IMemoryCache` (local in-process) layer in front of the distributed cache can significantly reduce network calls for frequently accessed hot data.
    *   **Optimized Serialization**: Smaller payloads mean faster network transfers.

2.  **Serialization Overhead**: Data needs to be serialized into bytes before being sent to the cache and deserialized when retrieved.
    *   **`System.Text.Json`**: Modern .NET offers highly optimized JSON serialization. It's human-readable and generally performant enough. For custom types, ensure you're using `JsonSerializerOptions` for optimal behavior (e.g., case insensitivity, `ReferenceHandler.Preserve` if needed).
    *   **Binary Formats (e.g., MessagePack)**: For extreme performance and minimal payload size, binary serialization can be superior, but it sacrifices human readability and can be less flexible for schema evolution.

3.  **Cache Eviction and Expiration**: Proper TTL (Time-To-Live) management is crucial.
    *   **Absolute Expiration**: Data expires after a fixed duration. Simple, but can lead to cache invalidation "storms."
    *   **Sliding Expiration**: Data expires if not accessed for a certain period. Good for infrequently accessed data that needs to stay fresh if used.
    *   **Proactive Refresh/Refresh-Ahead**: A background process or a "single-flight" mechanism can refresh data before it expires, reducing user-facing latency on cache misses.

4.  **Cache Stampede (Thundering Herd)**: When a cache item expires or is invalidated, and many concurrent requests hit the backend data source simultaneously. This can be mitigated using:
    *   **Distributed Locks**: A lightweight lock (e.g., Redlock with Redis) to ensure only one request regenerates the value.
    *   **SemaphoreSlim (local)** or **TaskCanceledSource/Lazy<Task>** combined with an asynchronous refresh pattern to ensure only one operation is fetching data for a given key locally.
    *   **Cache "Warm-up"**: Pre-populate critical cache entries on application startup or deploy.

### Architecting a Layered Distributed Cache in .NET

Let's look at a practical example of building a layered caching solution for a `Product` entity using `IMemoryCache` and `IDistributedCache` (backed by Redis) within a Minimal API. This combines the speed of in-process caching with the scalability of a distributed store.

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Define our product record (immutable and simple for serialization)
public record Product(int Id, string Name, decimal Price, DateTime LastUpdated);

// Simulate a database service
public class ProductDataService
{
    private readonly ILogger<ProductDataService> _logger;

    public ProductDataService(ILogger<ProductDataService> logger)
    {
        _logger = logger;
    }

    public async Task<Product?> GetProductFromDbAsync(int productId)
    {
        _logger.LogInformation("Fetching product {ProductId} from database...", productId);
        // Simulate database latency
        await Task.Delay(Random.Shared.Next(100, 300));
        if (productId % 2 == 0) // Simulate some products existing, some not
        {
            return new Product(productId, $"Product {productId}", 19.99m + productId, DateTime.UtcNow);
        }
        return null;
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Configure services
        builder.Services.AddMemoryCache(); // For IMemoryCache
        builder.Services.AddStackExchangeRedisCache(options => // For IDistributedCache
        {
            // Get connection string from appsettings.json or environment variables
            options.Configuration = builder.Configuration.GetConnectionString("RedisCache");
            options.InstanceName = "ProductCache_"; // Prefix keys to avoid conflicts
        });

        builder.Services.AddSingleton<ProductDataService>();
        builder.Services.AddLogging(config => config.AddConsole()); // Simple console logging

        var app = builder.Build();

        // Minimal API endpoint for fetching a product
        app.MapGet("/products/{id}", async (
            int id,
            IDistributedCache distributedCache,
            IMemoryCache memoryCache,
            ProductDataService dataService,
            ILogger<Program> logger) =>
        {
            var cacheKey = $"product:{id}";
            Product? product = null;

            // Step 1: Check local in-memory cache
            if (memoryCache.TryGetValue(cacheKey, out byte[]? localCachedBytes))
            {
                // Deserialize from byte[] stored in memory cache
                product = JsonSerializer.Deserialize<Product>(localCachedBytes);
                if (product != null)
                {
                    logger.LogInformation("Product {ProductId} found in local memory cache.", id);
                    return Results.Ok(product);
                }
            }
            
            // Step 2: Check distributed cache
            var distributedCachedBytes = await distributedCache.GetAsync(cacheKey);
            if (distributedCachedBytes != null)
            {
                product = JsonSerializer.Deserialize<Product>(distributedCachedBytes);
                if (product != null)
                {
                    logger.LogInformation("Product {ProductId} found in distributed cache.", id);
                    // Populate local cache for subsequent requests to this instance
                    memoryCache.Set(cacheKey, distributedCachedBytes, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) // Shorter local TTL
                    });
                    return Results.Ok(product);
                }
            }

            // Step 3: Data not in any cache, fetch from database
            logger.LogWarning("Product {ProductId} not found in cache. Fetching from data source.", id);
            product = await dataService.GetProductFromDbAsync(id);

            if (product != null)
            {
                // Serialize and store in both distributed and local caches
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(product);

                // Distributed cache options (longer TTL)
                var distributedCacheEntryOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
                };
                await distributedCache.SetAsync(cacheKey, jsonBytes, distributedCacheEntryOptions);
                
                // Local memory cache options (shorter TTL to ensure refresh from distributed)
                memoryCache.Set(cacheKey, jsonBytes, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                });

                logger.LogInformation("Product {ProductId} fetched from DB and cached.", id);
                return Results.Ok(product);
            }
            else
            {
                logger.LogWarning("Product {ProductId} not found in data source.", id);
                // Consider caching a 'not found' marker for a short period to prevent repeated DB hits
                // For simplicity, we're not doing that here.
                return Results.NotFound();
            }
        });

        app.Run();
    }
}
```

To run this example, you'd need a `appsettings.json` with a Redis connection string:
```json
{
  "ConnectionStrings": {
    "RedisCache": "localhost:6379,password=your_redis_password_here"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```
And the necessary NuGet packages:
`Microsoft.AspNetCore.App` (implicit for Minimal APIs)
`Microsoft.Extensions.Caching.StackExchangeRedis`

**Why this code is structured this way:**

*   **Dependency Injection**: `IDistributedCache`, `IMemoryCache`, `ProductDataService`, and `ILogger` are all injected. This promotes loose coupling, testability, and adherence to the D-principle of SOLID.
*   **Layered Caching**: We check `IMemoryCache` first, then `IDistributedCache`. This is a critical performance optimization. If the data is in `IMemoryCache`, we avoid *any* network hop. If it's in `IDistributedCache` but not `IMemoryCache` (perhaps due to a local eviction or a new instance handling the request), we fetch from Redis, then *populate the local cache*. This ensures that the next request to *this specific application instance* will hit the local cache.
*   **Serialization**: `System.Text.Json` is used for its excellent performance and native integration. We serialize to `byte[]` because `IDistributedCache` and `IMemoryCache` (when storing objects, it's safer to store raw bytes or strings) operate on byte arrays. This ensures efficient storage and retrieval.
*   **Distinct TTLs**: The `IMemoryCache` has a significantly shorter `AbsoluteExpirationRelativeToNow` (1 minute) than the `IDistributedCache` (10 minutes). This strategy means that if data changes, the local cache will expire quickly, forcing a check against the distributed cache. The distributed cache acts as the more authoritative, longer-lived cache.
*   **`ILogger`**: Crucial for observability. By logging cache hits and misses, we gain insight into the effectiveness of our caching strategy, helping us tune TTLs and identify potential issues.
*   **`ProductDataService` Abstraction**: Encapsulates the actual data fetching logic, making the cache interaction clean and the service mockable for testing.

This pattern demonstrates a robust, production-ready approach to handling data retrieval where performance and scalability are key.

### Common Pitfalls and Best Practices

Even with well-structured code, distributed caching introduces its own set of challenges.

**Pitfalls:**

*   **Stale Data Blindness**: Assuming cached data is always fresh. This leads to subtle bugs, especially in complex systems with multiple writers or microservices.
*   **Over-Caching**: Caching data that is rarely accessed or changes too frequently. This wastes cache resources and adds unnecessary complexity.
*   **Improper Key Strategy**: Keys that are not unique, too generic, or difficult to reconstruct. Versioning keys (e.g., `product:v2:{id}`) is often overlooked but critical for controlled invalidation on schema changes.
*   **Ignoring Cache Failures**: Assuming the cache is always available. A cache failure shouldn't bring down your entire application.
*   **Lack of Monitoring**: Without metrics on cache hit ratio, eviction rates, and latency, you're flying blind.

**Best Practices:**

*   **Define Clear Consistency Guarantees**: For most caching scenarios, eventual consistency is acceptable. Ensure your application logic handles potentially stale data gracefully. If strong consistency is required, a cache might not be the right solution, or you might need a write-through/write-behind pattern with a data grid.
*   **Robust Key Management**: Design your cache keys to be specific, predictable, and include relevant identifiers. Consider adding a version segment to keys to facilitate bulk invalidation or schema changes (e.g., `v2:products:{productId}`).
*   **Graceful Degradation**: Implement circuit breakers or fallback logic. If the distributed cache is unavailable, the application should bypass it and go directly to the database (possibly with increased logging and alerts). This is often called the "cache-aside" pattern, which naturally lends itself to this.
*   **Tune TTLs Aggressively**: Start with shorter TTLs and gradually increase them based on monitoring and business requirements. It's easier to relax a TTL than to fix a bug caused by stale data.
*   **Monitor Everything**: Track cache hit/miss ratios, average cache retrieval times, and cache eviction rates. Redis specifically offers a wealth of metrics. Integrate these into your observability stack.
*   **Handle Cache Stampede**: Implement distributed locks or a single-flight mechanism when regenerating expensive cache items. The `LazyCache` library is a good example for local stampede protection, but for distributed systems, a Redis-backed lock is often needed.
*   **Use Asynchronous APIs**: Always use the `async/await` patterns provided by `IDistributedCache` (e.g., `GetAsync`, `SetAsync`). Blocking calls can lead to thread pool starvation and reduced throughput in ASP.NET Core applications.

### Conclusion

Building a high-performance distributed caching solution in .NET is a non-trivial engineering task that goes beyond simply calling `IDistributedCache.SetAsync`. It requires a deep understanding of network implications, serialization trade-offs, consistency models, and effective fault tolerance. By carefully designing your caching strategy, implementing layered caching, choosing efficient serialization, and diligently monitoring your cache's performance, you can significantly enhance the scalability and responsiveness of your modern .NET applications. Remember, a cache is a performance optimization, but a poorly implemented one can introduce more problems than it solves. Build it thoughtfully, and observe its behavior rigorously.
