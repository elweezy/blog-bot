---
layout: post
title: "Building Resilient .NET HTTP Clients with IHttpClientFactory and Polly"
date: 2025-11-10 21:35:08 +0000
categories: dotnet blog
canonical_url: "https://dev.to/karnafun/building-a-secure-stripe-checkout-integration-with-aspnet-core-and-webhook-handling-ga3"
---

The phone buzzed, a familiar dread creeping in. "Production alert: ExternalUserService timeout exceeded." Not the first time, not the last. This particular API, a crucial dependency for our customer portal, had a knack for intermittent slowness, sometimes just outright drops. Before IHttpClientFactory, these outages meant a frantic dance of restarting services, checking server logs for connection exhaustion, and praying to the DNS gods that the cached entries would eventually refresh. It was brittle, exhausting, and frankly, amateur hour for a system handling millions of requests daily.

We've all been there. Building modern .NET applications, especially those embracing microservices or integrating with countless third-party APIs, means living in a world of distributed systems. And in distributed systems, failure isn't an exception; it's the default state. Networks glitch, APIs become momentarily unavailable, services get overloaded. Expecting perfect uptime from every dependency is a fantasy that will inevitably crash your carefully crafted application. This isn't just about robustness; it's about system stability, user experience, and ultimately, your sanity.

That's where `IHttpClientFactory` and Polly step in. For too long, `HttpClient` was a footgun in the hands of many .NET developers. Instantiating `new HttpClient()` in a `using` block for every request led to socket exhaustion and slow DNS resolution. Creating a singleton `HttpClient` risked stale DNS caches and other lifetime issues. `IHttpClientFactory`, introduced in .NET Core 2.1, finally provided a sane, managed approach to `HttpClient` lifecycle, pooling connections, and injecting configuration. But even a well-managed `HttpClient` can't magically make a flaky external service reliable. That's a job for resilience policies, and Polly is the undisputed champion in the .NET ecosystem for that.

### The Dynamic Duo: `IHttpClientFactory` and Polly

Let's unpack why these two are indispensable in your toolbox.

#### `IHttpClientFactory`: Your `HttpClient` Custodian

`IHttpClientFactory` isn't just a fancy way to get an `HttpClient` instance. It's a fundamental shift in how we manage HTTP client resources. It addresses several critical issues:

1.  **Connection Pooling and Lifecycle Management:** It pools `HttpClient` instances, preventing socket exhaustion and ensuring connections are reused efficiently. Crucially, it manages the underlying `HttpMessageHandler` instances, which are responsible for DNS resolution. `IHttpClientFactory` rotates these handlers periodically, gracefully tackling the stale DNS problem that plagued long-lived singleton `HttpClient` instances.
2.  **Centralized Configuration:** You can configure named or typed `HttpClient` instances, applying common settings like base addresses, default headers, and timeouts in one place.
3.  **Extensibility with `HttpClientFactory` Delegating Handlers:** This is where the magic really happens. `IHttpClientFactory` supports a pipeline of `DelegatingHandler` instances. These handlers can intercept outgoing requests and incoming responses, allowing you to inject cross-cutting concerns like logging, authentication, caching, and — you guessed it — resilience policies.

While `IHttpClientFactory` offers both named and typed clients, I almost always lean towards **Typed Clients**. They provide strong typing, encapsulation, and make your code significantly cleaner. Instead of injecting `IHttpClientFactory` and creating `HttpClient` instances manually, you inject your custom client class, which itself consumes a pre-configured `HttpClient`. It's a separation of concerns that pays dividends in maintainability.

#### Polly: Your Resilience Architect

Polly is a .NET resilience and transient-fault-handling library. It allows you to express policies such as Retry, Circuit Breaker, Timeout, Cache, and Fallback in a fluent and thread-safe manner. When combined with `IHttpClientFactory`, these policies become declarative additions to your HTTP client pipeline, rather than scattered `try-catch` blocks in your business logic.

The core policies you'll find yourself reaching for repeatedly are:

*   **Retry:** For transient failures. If a request fails due to a network glitch (a 500 server error, a connection reset), a retry policy will automatically re-attempt the request after a configurable delay. Crucially, you should always use **exponential back-off with jitter** to avoid overwhelming the failing service and to prevent "thundering herd" issues where all clients retry simultaneously.
*   **Circuit Breaker:** For non-transient or prolonged failures. If an external service is consistently failing, continuously retrying will only make matters worse for both your application and the struggling service. A circuit breaker monitors failures, and if a threshold is met, it "trips" open, preventing further requests to the failing service for a configurable duration. This gives the external service time to recover and prevents your application from exhausting its resources by waiting for doomed requests. After the break, it transitions to "half-open" to cautiously test if the service has recovered.
*   **Timeout:** For slow responses. Sometimes a service doesn't fail, it just takes forever. A timeout policy ensures that your application doesn't get stuck waiting indefinitely, tying up resources. You can configure a global timeout for the entire policy execution or a per-request timeout.

Composing these policies within `IHttpClientFactory` means your application code can focus on its core business logic, delegating the complex dance of error handling and resilience to a robust, well-tested library.

### Building it Out: A Production-Ready Example

Let's imagine we have a `UserService` that interacts with an external `User API`. We'll use a Typed HttpClient and configure `IHttpClientFactory` with Polly policies for retry, circuit breaking, and a global timeout. This example uses a Minimal API in ASP.NET Core, common in modern microservice architectures.

First, define our simple User DTO and the Typed HTTP client:

```csharp
// Models.cs
public record User(int Id, string Name, string Email);

// UserApiClient.cs
public class UserApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UserApiClient> _logger;

    public UserApiClient(HttpClient httpClient, ILogger<UserApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // BaseAddress can be set here or via IHttpClientFactory configuration in Program.cs
        _httpClient.BaseAddress ??= new Uri("https://api.externaluserservice.com");
    }

    public async Task<User?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Attempting to retrieve user {UserId} from external service.", userId);
            var response = await _httpClient.GetAsync($"/users/{userId}", cancellationToken);
            response.EnsureSuccessStatusCode(); // Throws HttpRequestException for 4xx/5xx

            return await response.Content.ReadFromJsonAsync<User>(cancellationToken: cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Polly will often handle transient HttpRequestExceptions, but we log here for context.
            // A common pattern is to rethrow a custom exception for business logic to handle specific API errors.
            _logger.LogError(ex, "Failed to retrieve user {UserId}: {Message}", userId, ex.Message);
            return null;
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "User retrieval for {UserId} was cancelled.", userId);
            throw; // Propagate cancellation for upstream handling
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            // This specific exception is thrown by Polly's Timeout policy if the HttpClient itself doesn't time out.
            _logger.LogError(ex, "Request to retrieve user {UserId} timed out via Polly policy.", userId);
            return null; // Or rethrow custom timeout exception
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            _logger.LogError(ex, "Request to retrieve user {UserId} blocked by circuit breaker.", userId);
            // The circuit breaker is open, so we know the service is likely down.
            // We might want to return cached data, default values, or a specific error response.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while retrieving user {UserId}.", userId);
            throw; // Re-throw for broader error handling
        }
    }
}
```

Now, let's configure `IHttpClientFactory` and Polly in `Program.cs`. Remember to install the `Polly` and `Polly.Extensions.Http` NuGet packages.

```csharp
// Program.cs
using System.Net;
using Polly;
using Polly.Extensions.Http;
using MinimalApiWithResilience; // Namespace where UserApiClient and User are defined

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- Polly Policy Configuration ---
        // A common strategy is to read policy parameters from configuration.
        // For simplicity, we'll hardcode them here.

        // 1. Retry Policy: Handles transient network errors and server-side issues (5xx status codes)
        //    with exponential backoff and jitter. Also retries on specific business error like 404 in this example.
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError() // Handles HttpRequestException, 5xx, and 408 status codes
            .OrResult(msg => msg.StatusCode == HttpStatusCode.NotFound) // Example: retry on 404 (if it's a transient condition)
            .WaitAndRetryAsync(
                retryCount: 5, // Maximum 5 retries
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(new Random().Next(0, 100)), // Exponential backoff with jitter
                onRetry: (outcome, timespan, retryAttempt, context) =>
                {
                    // Use a logger obtained via service provider for policy callbacks
                    builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>()
                        .LogWarning("Delaying for {Delay}ms, then retrying {Attempt}/{MaxAttempts}. Last status: {StatusCode}",
                            timespan.TotalMilliseconds, retryAttempt, 5, outcome.Result?.StatusCode);
                });

        // 2. Circuit Breaker Policy: Prevents repeatedly hitting a failing service, giving it time to recover.
        //    If 3 failures occur within a short window, the circuit opens for 30 seconds.
        var circuitBreakerPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3, // Number of consecutive failures to trip the circuit
                durationOfBreak: TimeSpan.FromSeconds(30), // How long the circuit stays open
                onBreak: (outcome, breakDelay) =>
                {
                    builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>()
                        .LogError("Circuit broken! All requests will now fail for {BreakDelay}ms. Last status: {StatusCode}",
                            breakDelay.TotalMilliseconds, outcome.Result?.StatusCode);
                },
                onReset: () =>
                {
                    builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>()
                        .LogInformation("Circuit reset.");
                },
                onHalfOpen: () =>
                {
                    builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>()
                        .LogInformation("Circuit is half-open, next request will test the service.");
                });

        // 3. Timeout Policy: Ensures requests don't hang indefinitely.
        var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(10)); // Timeout for the entire request operation

        // --- IHttpClientFactory Configuration ---
        builder.Services.AddHttpClient<UserApiClient>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri("https://api.externaluserservice.com"); // Set a default base address
                client.DefaultRequestHeaders.Add("Accept", "application/json"); // Example: default header
            })
            // Policies are added to the HttpClient pipeline. The order matters:
            // Policies are executed from the *inside out* relative to their addition.
            // So, 'timeoutPolicy' is the innermost (closest to the actual HTTP call).
            .AddPolicyHandler(timeoutPolicy) // Innermost: ensures each attempt respects the timeout
            .AddPolicyHandler(circuitBreakerPolicy)
            .AddPolicyHandler(retryPolicy); // Outermost: will retry the whole operation, including any nested policies

        // --- Standard ASP.NET Core setup for Minimal API ---
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();

        app.MapGet("/users/{id}", async (int id, UserApiClient userApiClient) =>
        {
            var user = await userApiClient.GetUserByIdAsync(id);
            return user is not null ? Results.Ok(user) : Results.NotFound();
        })
        .WithName("GetUserById")
        .WithOpenApi();

        app.Run();
    }
}
```

**Why this code matters:**

*   **Typed Client Encapsulation:** `UserApiClient` hides the `HttpClient` details, exposing a clean, high-level API. This makes your calling code (`app.MapGet`) simpler and less coupled to HTTP specifics.
*   **Declarative Resilience:** Notice how the `Program.cs` defines the resilience strategy entirely separately from the `UserApiClient`'s business logic. This is clean, centralized, and easy to modify without touching core application code.
*   **Policy Composition:** The `AddPolicyHandler` calls build a chain. The `timeoutPolicy` is added first, making it the innermost. This means *each individual attempt* (including retries) will respect the 10-second timeout. If an attempt times out, the `circuitBreakerPolicy` (next in line) will see it as a failure, and if the circuit is closed, the `retryPolicy` (outermost) might decide to retry the whole operation. This layered approach is powerful.
*   **Logging in Callbacks:** The `onRetry`, `onBreak`, etc., callbacks provide crucial observability. When a policy acts, you get immediate feedback in your logs, helping you understand how your system is handling external volatility. The slightly verbose `builder.Logging.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>()` is used because `Program.cs` is configuring services, and the full service provider isn't yet available when defining these static policies. In a more complex setup, you might encapsulate policies in a factory that takes an `ILogger<T>` directly.
*   **Real-world Considerations:** We handle `TaskCanceledException` (for caller cancellation) and specific Polly exceptions like `TimeoutRejectedException` and `BrokenCircuitException`, allowing for more nuanced error responses or fallbacks.

### Pitfalls to Avoid & Best Practices to Embrace

Building resilient systems isn't just about dropping in `IHttpClientFactory` and a few Polly policies. It requires thoughtful design.

**Common Pitfalls:**

*   **The "Fire and Forget" Timeout:** Just setting `HttpClient.Timeout` isn't enough. It only applies to the *first* attempt. Polly's `TimeoutPolicy` is superior as it can wrap an entire retry sequence.
*   **Ignoring Transient Errors:** Not all errors are equal. Retrying on a `401 Unauthorized` is pointless. Retrying on a `503 Service Unavailable` is essential. Polly's `HandleTransientHttpError()` is your friend here.
*   **Over-Retrying without Back-off:** Hammering a struggling service with immediate retries is a fast track to a DDoS attack on your dependency and a cascading failure for your own system. Always use exponential back-off with jitter.
*   **No Circuit Breaker:** The biggest mistake. Without a circuit breaker, your application will continuously attempt to call a dead service, exhausting its own thread pool, memory, and ultimately bringing itself down. Circuit breakers are your firebreak.
*   **Hardcoding Policy Parameters:** Resilience parameters like retry counts, back-off delays, and circuit breaker thresholds are often best managed via configuration. This allows tuning in different environments without code changes.
*   **Too Many Policies, Too Little Structure:** If every client has its own ad-hoc set of policies, maintenance becomes a nightmare. Centralize your policy definitions and reuse them.
*   **Lack of Observability:** If your policies are silently handling errors, you're flying blind. Log policy events (retries, circuit breaks, timeouts) and integrate them with your monitoring system.

**Best Practices:**

*   **Embrace Typed Clients:** They simplify `HttpClient` consumption and make your code more modular and testable.
*   **Configure Policies Centrally:** Define your Polly policies in `Program.cs` or a dedicated extension method. This keeps your application services clean.
*   **Use Policy Composition Wisely:** Think about the order. A good general pattern is: `Timeout` (for individual attempts) -> `Circuit Breaker` -> `Retry` (outermost, wrapping the others).
*   **Tailor Policies to Dependencies:** Different external services will have different reliability characteristics. A highly critical, rarely failing service might need a more aggressive circuit breaker, while a known-flaky but less critical one might need more retries.
*   **Test Your Resilience:** It's not enough to implement; you need to test. Use tools like WireMock.NET or simply throw `HttpRequestException` instances in a test environment to simulate failures and verify your policies behave as expected.
*   **Consider Fallbacks:** For non-critical data, a `FallbackPolicy` can return cached data or a default value, providing a degraded but still functional experience to the user when an external dependency is unavailable.
*   **Monitor Policy Metrics:** Polly can integrate with metrics systems (e.g., Application Insights, Prometheus) to provide insights into how often policies are being triggered, which circuits are open, etc. This is invaluable for understanding your system's health.

### The Unreliable Truth

The reality of distributed systems is that parts *will* fail. Your job as an architect and developer isn't to prevent every failure, but to design systems that can gracefully handle them. `IHttpClientFactory` and Polly, when used together thoughtfully, provide the bedrock for building .NET applications that don't just work, but work *resiliently*. They empower you to transform potential cascading failures into minor hiccups, ensuring your services remain stable and responsive, even when the world around them is anything but. Don't chase those timeout errors in production after the fact; build your defenses from the ground up.
