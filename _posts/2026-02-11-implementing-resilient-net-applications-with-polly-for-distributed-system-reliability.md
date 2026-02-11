---
layout: post
title: "Implementing Resilient .NET Applications with Polly for Distributed System Reliability"
date: 2026-02-11 04:00:38 +0000
categories: dotnet blog
canonical_url: "https://stackoverflow.com/questions/79886180/when-using-lambda-expressions-with-bulk-update-apis-for-ef-core-is-the-lambda-e"
---

The `HttpClient` call fails. Again. Not with a 500 internal server error, but a timeout. Then a `ConnectionRefused`. A few seconds later, it's a 408 Request Timeout. The external service is flaky, or perhaps just briefly overloaded. Left unchecked, this kind of intermittent unreliability rapidly cascades through a system, turning a minor hiccup in one service into a widespread outage across your application boundary. This is the reality of distributed systems: they *will* fail, and the network *will* be unreliable. The question isn't if, but when and how we mitigate it.

Modern .NET applications, especially those built on microservice architectures and deployed to the cloud, operate in environments where network latency, temporary service unavailability, and resource contention are constants. Simply letting exceptions bubble up means your application is brittle. This is where resilience strategies come into play, and in the .NET ecosystem, Polly stands as the battle-hardened library of choice for implementing these patterns effectively.

### The Imperative of Resilience in Cloud-Native .NET

The shift towards cloud-native architectures, containerization, and serverless functions has amplified the need for application resilience. Services are deployed independently, scaled elastically, and communicate over a network – often the public internet. This brings immense flexibility and scalability but also introduces new failure modes. A well-architected .NET system today must anticipate these failures rather than react to them as surprises.

Polly provides a fluent API to define and compose policies for handling transient faults. It integrates seamlessly with modern .NET features like `async/await` and `IHttpClientFactory`, making it a natural fit for building robust `HttpClient`-based communication layers. Rather than scattering `try-catch` blocks and manual retry loops throughout your codebase, Polly centralizes and standardizes these fault-handling mechanisms, improving both readability and maintainability.

Let's dissect the core resilience patterns that Polly empowers:

1.  **Retries:** The simplest and most common pattern. When a transient error occurs (e.g., a network blip, a temporary service overload), the operation is retried. But naive retries can exacerbate problems, leading to "thundering herd" issues. Smart retries involve:
    *   **Exponential backoff:** Waiting longer between successive retries.
    *   **Jitter:** Adding a random delay to backoff intervals to prevent synchronized retry storms.
    *   **Circuit Breaking:** Crucial for preventing cascading failures. If a service consistently fails, further calls to it are "tripped" (short-circuited) immediately for a specified duration, giving the failing service time to recover and preventing your application from wasting resources on doomed requests.
    *   **Timeouts:** Prevents operations from hanging indefinitely, tying up resources. Polly supports both:
        *   **Overall timeout:** The maximum time the *entire policy execution* (including retries) can take.
        *   **Individual attempt timeout:** The maximum time *each individual attempt* within a policy can take.

### Architecting Robust HTTP Client Communication

The most common place we implement resilience patterns in .NET is around outbound HTTP calls. With `IHttpClientFactory` in ASP.NET Core, integrating Polly policies becomes remarkably straightforward and elegant. It ensures proper `HttpClient` lifetime management and avoids common pitfalls like socket exhaustion, while also providing a natural extensibility point for Polly.

Consider a scenario where your application needs to fetch data from an external API that is occasionally unstable. We need retries for transient issues and a circuit breaker to protect our system if the external API goes down hard.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;
using System;
using System.Net.Http;
using System.Threading.Tasks;

public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            });
}

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Define a common HTTP transient fault policy strategy.
        // This policy combines Retries, Circuit Breaking, and Timeouts.
        var resiliencePolicy = Policy<HttpResponseMessage>
            // 1. Handle transient HTTP errors (5xx, 408)
            .HandleTransientHttpError()
            // 2. Handle specific exceptions (e.g., TimeoutRejectedException from Polly's Timeout policy)
            .Or<TimeoutRejectedException>()
            // 3. Configure exponential backoff retries with jitter
            .WaitAndRetryAsync(6, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(new Random().Next(0, 100)),
                (outcome, timespan, retryAttempt, context) =>
                {
                    // Log the retry attempt. Use context for correlation if needed.
                    context.GetLogger()?.LogWarning(
                        "Delaying for {delay}ms, then retrying {retryAttempt}. Status: {statusCode}. Exception: {exceptionType}",
                        timespan.TotalMilliseconds,
                        retryAttempt,
                        outcome.Result?.StatusCode.ToString() ?? "N/A",
                        outcome.Exception?.GetType().Name ?? "N/A");
                })
            // 4. Implement a circuit breaker
            .WrapAsync(Policy
                .HandleTransientHttpError()
                .Or<TimeoutRejectedException>()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 5,  // Number of consecutive failures before breaking
                    durationOfBreak: TimeSpan.FromSeconds(30), // How long the circuit stays open
                    onBreak: (outcome, timespan, context) =>
                    {
                        context.GetLogger()?.LogError("Circuit breaking! Opening circuit for {delay}ms. Status: {statusCode}. Exception: {exceptionType}",
                            timespan.TotalMilliseconds,
                            outcome.Result?.StatusCode.ToString() ?? "N/A",
                            outcome.Exception?.GetType().Name ?? "N/A");
                    },
                    onReset: (context) =>
                    {
                        context.GetLogger()?.LogInformation("Circuit reset. Closing circuit.");
                    },
                    onHalfOpen: () =>
                    {
                        context.GetLogger()?.LogInformation("Circuit half-open. Allowing one test call.");
                    }));

        // 5. Add an individual attempt timeout.
        // This is important to ensure *each* HTTP call doesn't hang indefinitely.
        // This policy is executed *before* retries and circuit breaking evaluate the result.
        var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(10, TimeoutStrategy.Optimistic,
            onTimeoutAsync: async (context, timespan, task) =>
            {
                context.GetLogger()?.LogWarning("HTTP request timed out after {delay} seconds (individual attempt).", timespan.TotalSeconds);
                // Optionally log the original task which might be faulted or canceled.
                await Task.CompletedTask; // Or log details from task.Exception if available
            });

        // Compose the policies. The timeout policy should wrap the actual HTTP call.
        // The resiliencePolicy (retries + circuit breaker) then wraps the timeout.
        // Order matters: Individual timeout -> (Actual HTTP Call) -> Retries -> Circuit Breaker
        // Polly's extension methods (AddPolicyHandler) make this composition natural.
        var finalPolicy = Policy.WrapAsync(resiliencePolicy, timeoutPolicy);


        services.AddHttpClient("ExternalService", client =>
        {
            client.BaseAddress = new Uri("http://localhost:5001"); // Example external service
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        // Use the AddPolicyHandler extension for IHttpClientFactory
        .AddPolicyHandler(finalPolicy)
        .AddPolicyHandler(request => // Another example: a static overall timeout for *each* configured client request
        {
             // This applies to the entire request lifecycle including retries,
             // and is configured *per client instance* rather than wrapping other policies.
             // This is an alternative to the `finalPolicy` composition above if you want
             // the overall timeout to apply *after* the Polly policies have run.
             // For simplicity, stick to the `finalPolicy` for now, but this demonstrates options.
             // For this example, let's say we want to enforce an absolute max time for the
             // entire operation including retries.
             return Policy.TimeoutAsync<HttpResponseMessage>(30); // 30 seconds max for the entire operation
        });


        // Register a named HTTP client without policies for comparison or other uses
        services.AddHttpClient("UnreliableServiceWithoutPolicy", client =>
        {
            client.BaseAddress = new Uri("http://localhost:5001");
        });

        // Add a logger to the Polly context for policy logging
        // This is a common pattern to pass DI-managed services into Polly's execution context.
        services.AddScoped(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new Context("PollyScopedContext")
            {
                {"Logger", loggerFactory.CreateLogger("PollyPolicy")}
            };
        });

        // Minimal API endpoint for demonstration
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/data", async (IHttpClientFactory httpClientFactory, Context pollyContext) =>
            {
                var client = httpClientFactory.CreateClient("ExternalService");
                pollyContext.GetLogger()?.LogInformation("Attempting to fetch data with resilient client...");
                try
                {
                    // Pass the logger through the Polly context for policy logging callbacks
                    // The 'pollyContext' here is the one registered via DI.
                    var response = await client.GetAsync("/api/data", pollyContext);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    return Results.Ok($"Data fetched successfully: {content.Substring(0, Math.Min(content.Length, 100))}...");
                }
                catch (BrokenCircuitException ex)
                {
                    pollyContext.GetLogger()?.LogError(ex, "Circuit is open! Too many failures.");
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable, "Service currently unavailable due to circuit breaker.");
                }
                catch (TimeoutRejectedException ex)
                {
                    pollyContext.GetLogger()?.LogError(ex, "Overall operation timed out.");
                    return Results.StatusCode(StatusCodes.Status504GatewayTimeout, "Operation timed out after multiple attempts.");
                }
                catch (HttpRequestException ex)
                {
                    pollyContext.GetLogger()?.LogError(ex, "Failed to fetch data after retries.");
                    return Results.StatusCode(StatusCodes.Status500InternalServerError, "Failed to communicate with external service.");
                }
            })
            .WithName("GetDataWithResilience")
            .WithOpenApi();

            endpoints.MapGet("/data-without-resilience", async (IHttpClientFactory httpClientFactory) =>
            {
                var client = httpClientFactory.CreateClient("UnreliableServiceWithoutPolicy");
                try
                {
                    var response = await client.GetAsync("/api/data");
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                    return Results.Ok($"Data fetched successfully without resilience: {content.Substring(0, Math.Min(content.Length, 100))}...");
                }
                catch (Exception ex)
                {
                    // This will likely fail quickly and directly on the first transient error.
                    return Results.StatusCode(StatusCodes.Status500InternalServerError, $"Failed without resilience: {ex.Message}");
                }
            })
            .WithName("GetDataWithoutResilience")
            .WithOpenApi();
        });
    }
}

// Extension to easily retrieve the logger from Polly's Context
public static class PollyContextExtensions
{
    public static ILogger? GetLogger(this Context context)
    {
        return context.TryGetValue("Logger", out object? logger) && logger is ILogger typedLogger
            ? typedLogger
            : null;
    }
}

// To run this example, you'd also need a simple external service running on port 5001.
// A minimal API that sometimes fails would be ideal:
/*
// Minimal External Service (e.g., in a separate project)
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5001"); // Important for the client to find it

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

var failureCount = 0;
app.MapGet("/api/data", async (HttpContext httpContext) =>
{
    failureCount++;
    if (failureCount % 2 == 1 || failureCount % 7 == 0) // Simulate intermittent failures
    {
        app.Logger.LogWarning("External service failing call {count}", failureCount);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsync("Simulated internal server error");
        return;
    }
    app.Logger.LogInformation("External service succeeding call {count}", failureCount);
    await Task.Delay(50); // Simulate some work
    await httpContext.Response.WriteAsync("{\"message\": \"Hello from external service!\"}");
});

app.Run();
*/
```

This example demonstrates several production-level patterns:

*   **Dependency Injection (`IServiceCollection`):** Polly policies are registered as part of `HttpClientFactory` configuration, making them centrally managed and reusable. The `Context` object for logging is also registered this way to tie into the DI container's logger.
*   **Minimal APIs:** The example uses the modern ASP.NET Core Minimal API endpoints for brevity and clarity, showcasing how policies integrate with typical web request flows.
*   **`IHttpClientFactory`:** The recommended way to create and manage `HttpClient` instances in .NET Core. `AddPolicyHandler` is the extension method that ties Polly policies directly into the `HttpClient`'s message handler pipeline.
*   **Policy Composition (`Policy.WrapAsync`):** We combine a `Retry` and `CircuitBreaker` policy (`resiliencePolicy`) and then wrap that with an `Individual Attempt Timeout` policy (`timeoutPolicy`). The order of policy application is crucial: the innermost policies affect the raw operation, and outer policies observe and react to their outcomes.
*   **Logging:** Callback delegates (`onBreak`, `onReset`, retry logging) use `ILogger` obtained from Polly's `Context` object, providing critical observability into policy execution. This is paramount for debugging and understanding system behavior under stress.
*   **Asynchronous Operations:** All policies are defined and executed asynchronously using `Async` suffixes, aligning with the non-blocking nature of modern .NET I/O operations.

The use of `HandleTransientHttpError()` is key, as it automatically configures Polly to handle HTTP status codes 5XX and 408 (Request Timeout), along with a set of common network-related exceptions. The addition of `Or<TimeoutRejectedException>()` ensures that failures from our *individual attempt timeout policy* also trigger retries and circuit breaking, treating a hanging request attempt as a fault.

### Pitfalls and Best Practices

While Polly is powerful, misapplication can create new problems or mask underlying architectural issues.

*   **Over-retrying Non-Idempotent Operations:** This is a classic blunder. Retrying an operation like "charge customer credit card" or "deposit funds" without idempotency checks can lead to double charges or incorrect state. Only retry operations that are truly idempotent (can be applied multiple times without changing the result beyond the initial application) or where the external system explicitly supports idempotent retries.
*   **Ignoring Overall System Capacity:** While retries help with transient faults, they don't magically fix an overloaded external service. If the service is genuinely struggling, aggressive retries can become a "thundering herd" scenario, exacerbating the problem. Circuit breakers are your primary defense here, giving the service breathing room.
*   **Hardcoding Policy Configuration:** Resilience parameters (retry counts, backoff times, circuit break durations) often need tuning based on observed production behavior. Hardcoding them prevents dynamic adjustments. Externalize these via configuration (e.g., `appsettings.json`, environment variables) and bind them to POCOs.
*   **Insufficient Logging and Monitoring:** Without logging policy events (like retries, circuit breaks, timeouts), you're flying blind. You won't know if your resilience mechanisms are even being triggered, let alone if they're effective. Integrate `ILogger` into your policy callbacks as shown in the example. Consider richer monitoring with metrics.
*   **Neglecting `Context` for Correlation:** The `Polly.Context` object is invaluable for passing request-specific data (like a correlation ID, user ID, or `ILogger` instance) through the policy chain. This helps trace issues across multiple retries or circuit breaker events.
*   **"Silver Bullet" Thinking:** Polly is a fantastic tool for *transient* fault handling. It is not a substitute for robust error handling, proper data validation, or fundamentally sound system design. If an external service consistently returns 400 Bad Request, Polly won't fix your input data. If your database schema is wrong, Polly won't correct it.

Resilience is not merely an afterthought; it's a fundamental design consideration in any distributed system. Polly, coupled with modern .NET features, provides a clear, concise, and robust framework for tackling the inherent unreliability of interconnected services. By thoughtfully applying these patterns, you empower your applications to gracefully navigate the storms of distributed computing, maintaining availability and a predictable user experience even when dependencies falter. It's about shifting from reactive firefighting to proactive architectural strength.
