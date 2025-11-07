---
layout: post
title: "Effective Caching and Instant Cache Invalidation"
date: 2025-11-07 13:13:38 +0000
categories: dotnet blog
canonical_url: "https://dev.to/karnafun/building-an-oauth2-protected-api-with-c-identityserver-and-aspnet-core-23g2"
---

You know the drill. You're building a sleek .NET API, and everything's humming along, fast and responsive. Then, naturally, you hit the database or an external service one too many times. "Ah-ha!" you exclaim, "Caching to the rescue!" You sprinkle some `IMemoryCache` magic, and suddenly your endpoints are blazing fast. Life is good.

Until it isn't.

Suddenly, that "blazing fast" endpoint is serving stale data. Maybe an admin revoked a user's permissions, but your authentication middleware, relying on a cached policy, still thinks they're valid. Or a critical configuration setting was updated, but your service keeps reading the old value. It's a subtle, insidious kind of bug that performance improvements often breed: the "my data is just... *wrong*" bug.

This isn't about *whether* to cache, it's about *how* to cache effectively, especially when data consistency and security are non-negotiable. We're talking about the holy grail: instant, programmatic cache invalidation for `IMemoryCache`.

### Why This Matters Now More Than Ever

In the world of modern .NET, especially with ASP.NET Core pushing us towards cloud-native, microservice architectures, performance is paramount. But so is reactivity. Users expect real-time updates; systems need immediate consistency.

1.  **Cloud-Native & Serverless:** Every millisecond counts when you're paying for compute on a per-request basis. Caching reduces external calls, cutting costs and latency. But if your functions or containers are stateless and scale rapidly, managing cache consistency across instances becomes a nightmare if you're not deliberate.
2.  **Security & Authorization:** As I mentioned, caching auth tokens or user roles without a robust invalidation strategy is a ticking time bomb. A user's access could be revoked, but a cached credential might keep them in the system for minutes or hours. That's a serious security vulnerability.
3.  **Real-time Data & UX:** Imagine a stock trading app, or an e-commerce site showing inventory levels. Stale data here isn't just an inconvenience; it can lead to financial loss or deeply frustrating user experiences.
4.  **Developer Experience (DX):** Dealing with mysterious stale data issues is a productivity killer. Having a clear, programmatic way to say "this cache entry is invalid *now*" makes debugging and reasoning about your application state much simpler.

Modern .NET 8/9 continues to push the envelope on performance, but it also empowers us with robust primitives. `IMemoryCache` is one of them, but its power lies not just in what it does out of the box, but in how we leverage its extensibility for tricky scenarios like instant invalidation.

### The Problem with `IMemoryCache` (and its Solution)

`IMemoryCache` is a fantastic in-process cache. It's fast, built-in, and leverages smart eviction policies. You can set an absolute expiration, a sliding expiration, or even a low-priority eviction if memory pressure is high. But what it *doesn't* do natively is give you a handle to say, "Hey, this specific item, identified by `my-key`, is obsolete *right this instant*. Get rid of it."

You *can* call `cache.Remove("my-key")`, of course. But that requires your code to know *when* to call `Remove`. What if the invalidation trigger comes from an external event – say, a message queue, a database trigger, or another service? You need a way to link that external event to the specific cache entry and invalidate it.

This is where `IChangeToken` and `CancellationTokenSource` come into play. This little trick is a game-changer for precise, on-demand invalidation.

The core idea is this: when you add an item to `IMemoryCache`, you can associate one or more `IChangeToken` instances with its `MemoryCacheEntryOptions`. If any of these tokens "fires" (indicates a change), the cache entry is immediately evicted. `CancellationChangeToken` (which wraps a `CancellationToken`) is our weapon of choice here because it gives us direct, programmatic control.

Here's how it works:

1.  When you cache an item, create a new `CancellationTokenSource` specifically for that item (or a group of related items).
2.  Pass its `Token` to a `CancellationChangeToken` and add it to the `MemoryCacheEntryOptions.ExpirationTokens` collection.
3.  When you want to invalidate that item, call `Cancel()` on its corresponding `CancellationTokenSource`.
4.  The `IMemoryCache` sees the cancellation, and *poof*, the item is gone.

It's like having a remote control for each cached item.

### Let's Get Our Hands Dirty with Some Code

I'm going to set up a simple ASP.NET Core Minimal API. Imagine we're caching some user profile data, and we need to invalidate it immediately if their profile is updated (or, crucially, their permissions change).

First, let's register `IMemoryCache` in our `Program.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives; // For CancellationChangeToken

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<UserProfileService>(); // Our service to manage profiles and cache

var app = builder.Build();

app.UseHttpsRedirection();

// Our API Endpoints
app.MapGet("/profile/{userId}", async (int userId, UserProfileService profileService) =>
{
    var profile = await profileService.GetProfileAsync(userId);
    return Results.Ok(profile);
})
.WithName("GetUserProfile")
.WithOpenApi();

app.MapPost("/profile/{userId}/update", async (int userId, string newEmail, UserProfileService profileService) =>
{
    // Simulate updating the profile in a database
    await Task.Delay(50); // Simulate DB write time
    Console.WriteLine($"Profile for user {userId} updated to email: {newEmail}");
    
    // Invalidate the cache for this user immediately
    profileService.InvalidateProfileCache(userId);

    return Results.Ok($"Profile for user {userId} updated and cache invalidated.");
})
.WithName("UpdateUserProfile")
.WithOpenApi();

app.Run();

// --- Services ---

public class UserProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "John Doe"; // Default for demo
    public string Email { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class UserProfileService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserProfileService> _logger;
    
    // This dictionary holds our CancellationTokenSources, one per cached user profile.
    // We use ConcurrentDictionary for thread-safety.
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _cacheCancellationTokens = new();

    public UserProfileService(IMemoryCache cache, ILogger<UserProfileService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    private static string GetCacheKey(int userId) => $"UserProfile:{userId}";

    public async Task<UserProfile> GetProfileAsync(int userId)
    {
        var cacheKey = GetCacheKey(userId);

        // Try to get profile from cache
        if (_cache.TryGetValue(cacheKey, out UserProfile? profile))
        {
            _logger.LogInformation("Cache HIT for UserProfile:{UserId}", userId);
            return profile!;
        }

        _logger.LogInformation("Cache MISS for UserProfile:{UserId}. Fetching from source...", userId);

        // Simulate fetching from a database or external service
        await Task.Delay(200); 
        profile = new UserProfile 
        { 
            Id = userId, 
            Email = $"user{userId}@example.com",
            LastUpdated = DateTime.UtcNow
        }; 

        // Get or create a CancellationTokenSource for this cache entry
        // This ensures we always have a CTS to cancel when we need to invalidate this specific entry.
        var cts = _cacheCancellationTokens.GetOrAdd(userId, _ => new CancellationTokenSource());

        var cacheEntryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5)) // Cache for 5 minutes by default
            .SetSlidingExpiration(TimeSpan.FromMinutes(1))  // Slide for 1 minute
            .AddExpirationToken(new CancellationChangeToken(cts.Token)); // THIS IS THE MAGIC!

        _cache.Set(cacheKey, profile, cacheEntryOptions);
        _logger.LogInformation("UserProfile:{UserId} added to cache.", userId);

        return profile;
    }

    public void InvalidateProfileCache(int userId)
    {
        var cacheKey = GetCacheKey(userId);

        if (_cacheCancellationTokens.TryRemove(userId, out var cts))
        {
            _logger.LogInformation("Invalidating cache for UserProfile:{UserId} via CancellationTokenSource.", userId);
            cts.Cancel(); // Triggers the eviction
            cts.Dispose(); // Clean up the CancellationTokenSource
        }
        else
        {
            _logger.LogInformation("No active CancellationTokenSource found for UserProfile:{UserId}.", userId);
            // Optionally, remove the item directly if no CTS was found (e.g., it expired naturally)
            // _cache.Remove(cacheKey); 
        }
    }
}
```

**How to test it:**

1.  Run the application.
2.  Open your browser or an API client.
3.  Call `GET /profile/123`. You'll see a cache miss the first time, then hits on subsequent calls. Note the `LastUpdated` timestamp.
4.  Call `POST /profile/123/update` with `newEmail=new@example.com`.
5.  Immediately call `GET /profile/123` again. You'll observe a cache miss, and the `LastUpdated` timestamp will reflect the update (or at least a fresh fetch). The previous cached entry was *instantly* evicted.

This pattern allows you to invalidate a single, specific item with precision, decoupling the caching logic from the data update logic.

### Pitfalls, Gotchas, and Best Practices

While this `CancellationTokenSource` approach is powerful, it's not without its considerations.

1.  **Memory Management of `CancellationTokenSource`**: This is the biggest gotcha. If you're creating a `CancellationTokenSource` for *every* cached item and you have millions of items, you're going to consume a lot of memory.
    *   **Solution**: Group `CancellationTokenSource` instances. For example, instead of one per `UserProfile`, you might have one per `Tenant` or `Region` if an update to one user invalidates all users within that group. Or, only use this pattern for *critical* data where instant invalidation is a must, letting less critical data expire naturally. In my example, I'm cleaning up the `CancellationTokenSource` when it's cancelled, which is crucial. If the cache entry expires naturally, the `CancellationTokenSource` will remain in the `_cacheCancellationTokens` dictionary. You'd need a mechanism to clean up these "orphaned" CTSs, perhaps using a `PostEvictionCallback` or a background service.
2.  **Distributed vs. In-Process**: Remember, `IMemoryCache` is *in-process*. This means if you have multiple instances of your API running (e.g., in Kubernetes, multiple VMs), invalidating the cache on one instance does *not* invalidate it on others.
    *   **Solution**: For distributed scenarios, you *must* use a distributed cache (like Redis). The `CancellationTokenSource` pattern is still valuable for L1 *local* caches. Your update operation would then publish a message to a message broker (e.g., RabbitMQ, Kafka, Azure Service Bus) or Redis Pub/Sub, which *each* instance subscribes to. Upon receiving the message, each instance would then call its `InvalidateProfileCache()` method, effectively invalidating its local `IMemoryCache`.
3.  **Cache Stampede**: When an item is invalidated, multiple concurrent requests might try to re-populate the cache simultaneously, leading to redundant (and expensive) calls to your data source.
    *   **Solution**: Use a locking mechanism (like a `SemaphoreSlim` or `Lazy` initialization) when populating the cache. If multiple threads ask for the same item that's currently being fetched, they wait for the first fetch to complete and then use its result.
4.  **Over-complication**: Not everything needs instant invalidation. For data that changes infrequently or where a few minutes of staleness is acceptable, standard `SetAbsoluteExpiration` or `SetSlidingExpiration` is perfectly fine and simpler.
5.  **Clean-up of `CancellationTokenSource`**: As hinted in point 1, `CancellationTokenSource` implements `IDisposable`. If you don't dispose them after they've been cancelled (or if the cache entry expires naturally without explicit invalidation), you can leak memory. The `InvalidateProfileCache` method in my example calls `Dispose()` when the CTS is used for invalidation. For natural expirations, you'd need a more advanced cleanup strategy, potentially using `PostEvictionCallbacks` on `MemoryCacheEntryOptions` to signal your `UserProfileService` when an item is evicted, so it can remove and dispose its associated `CancellationTokenSource`.

### Conclusion: Balance is Key

Effective caching is always a balancing act: performance versus freshness. `IMemoryCache`, when combined with `IChangeToken` and `CancellationTokenSource`, gives us a powerful tool to achieve instant, programmatic invalidation for critical data within a single process.

It empowers you to build highly performant applications without sacrificing data consistency or security. Just remember the distributed nature of modern applications and choose the right tool for the job. For in-process, surgical invalidation, this pattern is golden. For cross-instance invalidation, layer a distributed cache and a pub/sub mechanism on top.

Go forth, cache wisely, and keep your data fresh! What's your go-to strategy for cache invalidation in complex systems? I'd love to hear your war stories and solutions in the comments below.
