---
layout: post
title: "Building Resilient API Clients in .NET: Advanced HttpClient Usage and Error Handling"
date: 2025-12-24 03:35:07 +0000
categories: dotnet blog
canonical_url: "https://stackoverflow.com/questions/79853483/sqlbulkcopy-is-failing-to-save-records-with-an-error-the-given-columnmapping-do"
---

Network calls are a fact of life for most modern applications, especially within the increasingly distributed architectures we build today. Services communicate, data flows between boundaries, and inevitably, external systems will falter. Throttling, transient network glitches, unresponsive endpoints, or outright service outages are not "if" but "when." Relying on a naive `HttpClient` usage pattern in such an environment is akin to sailing without a life vest – you might be fine for a while, but eventually, you'll find yourself underwater.

For years, a recurring nightmare in the .NET ecosystem was the `HttpClient` instance management. The classic anti-pattern of instantiating a new `HttpClient` for every request led to socket exhaustion and port starvation. Conversely, the "solution" of a static, long-lived `HttpClient` instance, while preventing socket issues, came with its own set of problems, primarily the inability to react to DNS changes, leading to stale connections. This dichotomy made robust external API communication a perpetual architectural headache.

The introduction of `IHttpClientFactory` in .NET Core (and its subsequent refinement) was a pivotal moment, finally providing a robust, opinionated, and DI-friendly mechanism for managing `HttpClient` instances. It elegantly handles connection pooling, DNS updates, and, crucially, provides hooks for applying cross-cutting concerns like resilience policies. This isn't just a convenience API; it's a foundational piece for building truly resilient cloud-native applications in .NET.

### Architecting for Impermanence: Beyond Basic HTTP

Modern systems demand more than just making a request and handling a `200 OK`. They need to anticipate failure. This is where a layered approach to `HttpClient` usage becomes critical, incorporating retry policies, circuit breakers, and comprehensive error handling and logging.

**Retry Policies:** Not all errors are fatal. A `503 Service Unavailable` or a network timeout might just be a temporary glitch. A well-configured retry policy can transparently handle these transient issues, often making them invisible to the caller. The key is intelligent retrying:
*   **What to retry:** Only idempotent operations, and only for transient error codes (e.g., 408, 429, 500, 502, 503, 504).
*   **Backoff strategy:** Essential to prevent overwhelming a struggling service. Exponential backoff is the standard.
*   **Jitter:** Adding a small random delay to the backoff helps prevent the "thundering herd" problem where multiple clients retry at precisely the same interval.

**Circuit Breakers:** While retries help with transient issues, continuously retrying a consistently failing service only exacerbates the problem, consuming resources on both ends and potentially causing cascading failures across your own services. This is where circuit breakers shine. They monitor the health of an external service. If it fails too often, the circuit "opens," short-circuiting further requests to that service for a predefined period. This gives the failing service time to recover and prevents your system from wasting resources on doomed calls. After a timeout, the circuit enters a "half-open" state, allowing a limited number of test requests to see if the service has recovered.

**Timeouts:** Often overlooked, but critical. Every external call *must* have a reasonable timeout. Unbounded waiting for a response from a dead service is a guaranteed way to exhaust your own thread pool or block critical resources.

### A Production-Grade Client Implementation

Let's illustrate how we combine `IHttpClientFactory`, Polly (a popular .NET resilience library), and structured logging within a typical ASP.NET Core application. The goal is a client that's easy to use, handles failures gracefully, and provides clear diagnostic information when things inevitably go wrong.

Consider a scenario where our service needs to fetch data from an external `WeatherService` API.

```csharp
// Program.cs
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;
using Serilog;

// Configure Serilog for structured logging
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // Add services to the container.
    builder.Services.AddHttpClient<IWeatherApiClient, WeatherApiClient>(client =>
    {
        client.BaseAddress = new Uri("https://api.weatherapi.com/v1/");
        client.DefaultRequestHeaders.Add("User-Agent", "ResilientApiClientApp/1.0");
    })
    // Apply resilience policies using AddStandardResilienceHandler
    // This is the modern way in .NET 8+ to add common resilience patterns.
    // Behind the scenes, it uses Polly policies.
    .AddStandardResilienceHandler(options =>
    {
        // Customize the standard retry policy
        options.Retry.RetryCount = 3;
        options.Retry.Delay = TimeSpan.FromSeconds(1); // Initial delay
        options.Retry.BackoffType = DelayBackoffType.ExponentialWithJitter;
        options.Retry.Use.Add(new CircuitBreakerPolicyHandler()); // Example: Add circuit breaker
        options.Retry.ShouldHandle((outcome) =>
            outcome.Result?.StatusCode == HttpStatusCode.RequestTimeout ||
            outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests ||
            outcome.Result?.StatusCode >= HttpStatusCode.InternalServerError);

        // Customize the standard circuit breaker policy
        options.CircuitBreaker.FailureRatio = 0.5; // Break if 50% of requests fail
        options.CircuitBreaker.MinimumThroughput = 10; // Only break if at least 10 requests are made
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10); // How long to stay open
        
        // Add a timeout policy for the entire request pipeline
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    // Minimal API endpoint
    app.MapGet("/weather/{city}", async (string city, IWeatherApiClient weatherApiClient) =>
    {
        try
        {
            var weather = await weatherApiClient.GetCurrentWeatherAsync(city);
            return Results.Ok(weather);
        }
        catch (HttpRequestException httpEx)
        {
            Log.Error(httpEx, "HTTP request failed while fetching weather for {City}. Status: {StatusCode}", city, httpEx.StatusCode);
            return Results.StatusCode((int)httpEx.StatusCode);
        }
        catch (TimeoutRejectedException timeoutEx)
        {
            Log.Warning(timeoutEx, "External Weather API call timed out for {City}.", city);
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (BrokenCircuitException circuitEx)
        {
            Log.Warning(circuitEx, "Weather API circuit breaker is open for {City}.", city);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An unexpected error occurred while fetching weather for {City}.", city);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    })
    .WithName("GetWeather")
    .WithOpenApi();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}


// Interfaces and DTOs
public interface IWeatherApiClient
{
    Task<WeatherForecast?> GetCurrentWeatherAsync(string city);
}

public class WeatherApiClient : IWeatherApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WeatherApiClient> _logger;
    private readonly string _apiKey; // Best practice: get from config or secrets

    public WeatherApiClient(HttpClient httpClient, IConfiguration configuration, ILogger<WeatherApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiKey = configuration["WeatherApiKey"] ?? throw new ArgumentException("Weather API key not configured.");
    }

    public async Task<WeatherForecast?> GetCurrentWeatherAsync(string city)
    {
        _logger.LogInformation("Fetching weather for {City}...", city);

        var requestUri = $"current.json?key={_apiKey}&q={Uri.EscapeDataString(city)}";
        var response = await _httpClient.GetAsync(requestUri);

        // This would typically involve more robust response handling,
        // checking for specific error bodies, etc.
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var weatherData = JsonSerializer.Deserialize<WeatherApiResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (weatherData?.Current != null)
            {
                return new WeatherForecast(
                    DateOnly.FromDateTime(DateTime.UtcNow),
                    (int)weatherData.Current.TempC,
                    weatherData.Current.Condition.Text
                );
            }
            return null;
        }
        else
        {
            // Log full response details for non-success status codes
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Weather API call failed for {City}. Status: {StatusCode}, Response: {ErrorResponse}",
                city, response.StatusCode, errorContent);
            
            // Re-throw or throw a more specific exception for upstream handling
            response.EnsureSuccessStatusCode(); // This will throw HttpRequestException
            return null; // Should not be reached
        }
    }
}

// Simple DTOs for the example
public record WeatherForecast(DateOnly Date, int TemperatureC, string Summary);

public record WeatherApiResponse(Location Location, CurrentWeather Current);
public record Location(string Name, string Region, string Country);
public record CurrentWeather(double TempC, Condition Condition);
public record Condition(string Text, string Icon, int Code);

// Custom Policy Handler (for demonstration, often not needed with AddStandardResilienceHandler)
// This shows how you could hook into Polly's policy execution for custom logging or metrics.
public class CircuitBreakerPolicyHandler : DelegatingHandler
{
    private readonly ILogger<CircuitBreakerPolicyHandler> _logger;

    public CircuitBreakerPolicyHandler(ILogger<CircuitBreakerPolicyHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogWarning(ex, "Circuit breaker tripped for request to {RequestUri}. Service is unavailable.", request.RequestUri);
            throw;
        }
        catch (TimeoutRejectedException ex)
        {
            _logger.LogWarning(ex, "Request to {RequestUri} timed out.", request.RequestUri);
            throw;
        }
    }
}
```

In this example, we:
1.  **Configure `Serilog`:** Essential for structured, contextual logging. We use `builder.Host.UseSerilog` for early and consistent logging.
2.  **Register `IHttpClientFactory`:** Using `AddHttpClient<TClient, TImplementation>` automatically sets up a named client and manages the underlying `HttpClient` instance lifecycle.
3.  **Apply Resilience with `AddStandardResilienceHandler`:** This is the key. In .NET 8+, `Microsoft.Extensions.Http.Resilience` simplifies applying common Polly policies. We configure:
    *   **Retry:** 3 retries with exponential backoff and jitter for transient errors (timeouts, 429s, 5xx errors).
    *   **Circuit Breaker:** Open if 50% of requests fail within a 30-second window, for a minimum of 10 requests. Stays open for 10 seconds.
    *   **Total Request Timeout:** A hard 10-second limit for the entire operation, including retries.
4.  **Inject and Use:** `WeatherApiClient` simply injects `HttpClient` via its constructor, unaware of the resilience policies applied upstream. This separation of concerns is powerful for maintainability.
5.  **Robust Error Handling in API:** The minimal API endpoint `MapGet("/weather/{city}")` catches specific Polly exceptions (`TimeoutRejectedException`, `BrokenCircuitException`) and `HttpRequestException` for granular logging and returning appropriate HTTP status codes (e.g., 504 Gateway Timeout, 503 Service Unavailable). This is crucial for distinguishing between different failure modes.
6.  **Detailed Logging:** Inside `WeatherApiClient`, we log not just `HttpRequestException` but also the full error response body for non-success status codes. This level of detail is invaluable during debugging.

This pattern exemplifies a modern, production-ready API client. The `AddStandardResilienceHandler` dramatically simplifies the policy setup, leveraging battle-tested patterns within Polly without requiring direct Polly API calls in many common scenarios. This abstracts away the complexity of policy chain construction.

### Pitfalls and Hard-Learned Lessons

1.  **The Naked `new HttpClient()`:** Still the most common mistake. Don't do it. Ever. Use `IHttpClientFactory`.
2.  **Over-aggressive Retries:** Retrying too often or too quickly without backoff can turn a single failing dependency into a distributed denial-of-service attack on that dependency. Be mindful of the service's capacity.
3.  **Untamed Timeouts:** No explicit timeout configured means your service can hang indefinitely, consuming resources. Every HTTP call, local or remote, needs a timeout.
4.  **Silent Failures:** Not logging sufficient context or details when an external call fails makes debugging a nightmare. Include correlation IDs, request URLs, status codes, and ideally, relevant parts of the error response body.
5.  **Ignoring Idempotency:** If your operation isn't idempotent (meaning it can be safely executed multiple times with the same outcome), retries can lead to duplicate data or incorrect state. Be extremely careful applying retries to non-idempotent `POST` or `PUT` requests that lack idempotency keys.
6.  **Blindly Trusting `response.IsSuccessStatusCode`:** While useful, it might mask crucial business-level errors returned with a `200 OK`. Always parse the response body, even on success, if the API contract allows for embedded errors.

Building resilient API clients isn't just about adding some `try-catch` blocks or slapping on a retry policy. It's an architectural mindset shift, acknowledging the inherent unreliability of network communication. `IHttpClientFactory` and the resilience patterns it enables (especially with libraries like Polly and the new `AddStandardResilienceHandler`) provide the toolkit to construct reliable systems that can gracefully degrade rather than catastrophically fail, making them indispensable components in any modern .NET application interacting with external services.
