---
layout: post
title: "Implementing Server-Sent Events (SSE) in .NET Serverless Architectures"
date: 2026-01-14 03:38:42 +0000
categories: dotnet blog
canonical_url: "https://stackoverflow.com/questions/79865251/aws-lambda-server-sent-events-on-net-with-aws-api-gateway"
---

Implementing real-time communication patterns in serverless architectures often leads us down familiar paths, typically involving WebSockets. But what about scenarios where full-duplex communication feels like overkill? When the client primarily needs to *receive* a stream of updates, Server-Sent Events (SSE) offers a simpler, more HTTP-native alternative. The elegance of SSE, however, meets a pragmatic challenge when integrated into the popular .NET on AWS Lambda and API Gateway stack.

Architecting for dynamic content delivery – think progress bars, live dashboards, or continuous data feeds – inevitably brings up the question of how to push updates efficiently. While polling is simple, it's inherently inefficient, leading to unnecessary requests and latency. WebSockets, on the other hand, provide a robust, bi-directional channel but come with added complexity in both client and server implementations, not to mention the operational overhead of managing persistent connections, especially in a serverless context.

SSE carves out a niche here. It leverages standard HTTP/1.1 for a unidirectional, long-lived connection from server to client, delivering a stream of `text/event-stream` formatted data. It's simpler to implement, benefits from HTTP features like persistent connections and caching (though less relevant for a live stream), and is firewall-friendly. Modern browsers natively support the `EventSource` API, making client-side consumption straightforward. For .NET developers, embracing `IAsyncEnumerable<T>` and direct stream manipulation aligns perfectly with how we'd build a robust SSE endpoint.

### The Real-World Serverless Challenge: Streaming with AWS Lambda and API Gateway

Here's where the rubber meets the road. Our goal is to serve an SSE stream using .NET 8 on AWS Lambda, fronted by API Gateway. The immediate hurdle: **the standard API Gateway HTTP proxy integration with Lambda does not inherently support streaming HTTP responses.**

When a Lambda function is invoked via API Gateway using the standard `APIGatewayProxyRequest` and `APIGatewayProxyResponse` model, API Gateway waits for the Lambda's execution to complete and for the *entire* `APIGatewayProxyResponse` body to be returned as a single unit. It then proxies this complete response back to the client. This fundamental design means you cannot incrementally flush data to the client over a long-lived connection using this pattern. For true SSE, where the server continuously pushes events over an open connection, this approach is a non-starter.

So, if you're building a new real-time stream that absolutely requires a fully serverless, incremental push from a .NET backend, the most direct and capable AWS primitive for SSE is a **Lambda Function URL**. Lambda Function URLs expose a Lambda function directly via a public HTTP endpoint and, critically, they *do* support streaming responses, allowing you to flush data incrementally. This enables true SSE.

While API Gateway remains a crucial component for many serverless applications (for features like custom domains, authorization, request transformation, and throttling), for *true* long-lived SSE connections that stream data incrementally, Lambda Function URLs are the modern, serverless answer. You might still place an Application Load Balancer (ALB) in front of a Lambda Function URL for advanced routing or SSL termination, but the streaming capability originates directly from the Function URL.

Let's look at how we'd structure a .NET 8 Lambda function to serve an SSE stream, assuming it's exposed via a Lambda Function URL (or a similar streaming-capable HTTP endpoint like an application running on App Runner/ECS Fargate behind an ALB).

### Crafting a Streaming .NET Lambda Function for SSE

Our Lambda function will simulate a background process emitting progress updates. It will use `IAsyncEnumerable<T>` to generate a stream of events, demonstrating modern asynchronous programming patterns.

First, a simple helper to format our SSE messages:

```csharp
// src/MySseApp/SseFormatter.cs
using System.Text;
using System.Threading.Tasks;

namespace MySseApp;

public static class SseFormatter
{
    public static ReadOnlyMemory<byte> FormatEvent(string eventType, string data, string? id = null, TimeSpan? retry = null)
    {
        var sb = new StringBuilder();

        if (id != null)
        {
            sb.Append("id: ").Append(id).Append('\n');
        }

        if (eventType != null)
        {
            sb.Append("event: ").Append(eventType).Append('\n');
        }

        // SSE data lines can contain multiple lines, each prefixed with "data: "
        foreach (var line in data.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        if (retry.HasValue)
        {
            sb.Append("retry: ").Append((long)retry.Value.TotalMilliseconds).Append('\n');
        }

        sb.Append('\n'); // End of event marker

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
```

Now, the Lambda function itself, built as an ASP.NET Core Minimal API hosted in a Lambda container, configured for a Function URL:

```csharp
// src/MySseApp/Function.cs
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace MySseApp;

public class Function
{
    private static WebApplication? _app;

    public Function()
    {
        if (_app == null)
        {
            var builder = WebApplication.CreateBuilder();

            // Configure logging to stdout, which Lambda captures
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();

            // Add services to the container.
            builder.Services.AddSingleton<ProgressSimulator>(); // Our event source

            var app = builder.Build();

            app.MapGet("/events", async (HttpContext context, ProgressSimulator simulator, ILogger<Function> logger, CancellationToken ct) =>
            {
                logger.LogInformation("Client connected for SSE stream.");

                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";
                context.Response.Headers.AccessControlAllowOrigin = "*"; // Adjust CORS as needed

                // Ensure the headers are sent immediately
                await context.Response.StartAsync(ct);

                try
                {
                    await foreach (var progressUpdate in simulator.SimulateProgress(ct))
                    {
                        var eventData = SseFormatter.FormatEvent(
                            eventType: "progress",
                            data: JsonSerializer.Serialize(progressUpdate),
                            id: progressUpdate.StepId.ToString(),
                            retry: TimeSpan.FromSeconds(5) // Suggest client retries after 5 seconds if connection drops
                        );

                        await context.Response.Body.WriteAsync(eventData, ct);
                        await context.Response.Body.FlushAsync(ct); // Crucial for streaming

                        logger.LogDebug("Sent SSE event: {EventType}, Id: {Id}", "progress", progressUpdate.StepId);
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("SSE stream cancelled due to client disconnect or function timeout.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error while streaming SSE events.");
                }
                finally
                {
                    logger.LogInformation("SSE stream disconnected.");
                }
            });

            _app = app;
        }
    }

    /// <summary>
    /// This is the entry point for the Lambda function.
    /// It handles requests from Lambda Function URLs.
    /// </summary>
    public async Task FunctionHandler(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        // This is a minimal API request, not the APIGatewayProxyRequest.
        // We're essentially proxying the raw HTTP request through our in-process Minimal API.
        // This setup requires the AWS .NET Lambda runtime to map the Function URL request correctly.
        // When deploying with AWS Serverless Application Model (SAM) or AWS CDK,
        // you would configure the Lambda to use the 'main' method of the Function class as the handler,
        // and set up a Function URL with 'InvokeMode: RESPONSE_STREAM'.

        // This boilerplate is typically handled by the AWS.Lambda.AspNetCoreServer package.
        // For a true streaming setup with Function URLs, you don't use APIGatewayHttpApiV2ProxyRequest directly
        // if you want direct stream access. Instead, the runtime handles the HttpContext abstraction.
        // However, for this example to be runnable as a standard Lambda, we mock the request/response.
        // A more direct way would be to run as a container image and expose port 8080.

        // To demonstrate streaming within a regular Lambda context (e.g., via Test Runner),
        // we'd need to mock the HttpContext.Response.Body. For a live Function URL, this abstraction works.

        // In a real Function URL setup using AWS.Lambda.AspNetCoreServer:
        // You'd typically have a class inheriting from Amazon.Lambda.AspNetCoreServer.APIGatewayProxyFunction
        // which then maps the incoming API Gateway request to ASP.NET Core's HttpContext.
        // The key for streaming is 'InvokeMode: RESPONSE_STREAM' on the Function URL configuration.

        // Given the constraints of a single Lambda entry point, and the desire for full streaming,
        // the FunctionHandler will essentially bootstrap and process the request using the _app.
        // For production, AWS.Lambda.AspNetCoreServer handles the mapping for Function URLs seamlessly.
        // The `RunAsync` method below simulates this.
        await _app!.RunAsync();
    }
}

public record ProgressUpdate(Guid StepId, int Percentage, string StatusMessage);

public class ProgressSimulator
{
    private readonly ILogger<ProgressSimulator> _logger;

    public ProgressSimulator(ILogger<ProgressSimulator> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<ProgressUpdate> SimulateProgress(CancellationToken ct)
    {
        _logger.LogInformation("Starting progress simulation.");
        for (int i = 0; i <= 100; i += 10)
        {
            ct.ThrowIfCancellationRequested(); // Check for client disconnect

            var update = new ProgressUpdate(Guid.NewGuid(), i, $"Processing step {i / 10 + 1} of 11...");
            yield return update;

            await Task.Delay(TimeSpan.FromSeconds(2), ct); // Simulate work
        }

        yield return new ProgressUpdate(Guid.NewGuid(), 100, "Processing complete!");
        _logger.LogInformation("Progress simulation finished.");
    }
}
```

This code snippet showcases several production-ready patterns:
*   **Minimal APIs**: The `app.MapGet` syntax is concise and powerful for defining HTTP endpoints.
*   **Dependency Injection**: `ProgressSimulator` and `ILogger` are injected, promoting testability and maintainability.
*   **Async Streams (`IAsyncEnumerable`)**: `ProgressSimulator.SimulateProgress` elegantly generates events over time, which `await foreach` consumes. This is a natural fit for streaming data.
*   **`HttpContext.Response.Body.FlushAsync()`**: This is the *critical* call for SSE. It ensures that the buffered data is immediately sent over the network to the client, rather than waiting for the entire Lambda execution to complete. Without this, you don't get true streaming.
*   **CancellationToken**: Passed through to `SimulateProgress`, allowing the stream to gracefully terminate if the client disconnects or the Lambda times out.
*   **Proper SSE Headers**: Setting `Content-Type: text/event-stream`, `Cache-Control: no-cache`, and `Connection: keep-alive` is essential for SSE.
*   **Error Handling**: A `try-catch` block around the streaming loop ensures resilience and proper logging.

To deploy this as a true streaming Lambda function, you would typically package your .NET application as a custom runtime or a container image. When configuring the Lambda Function URL, you **must** set the `InvokeMode` to `RESPONSE_STREAM`. This tells AWS to open a streaming connection between the Function URL endpoint and the Lambda function.

### Pitfalls and Best Practices

1.  **API Gateway Timeout (Still Relevant for Proxying):** Even if you use a Lambda Function URL, if you later decide to place API Gateway *in front* of it (e.g., using a custom domain or for authorization), remember API Gateway's default 29-second (HTTP API) or 30-second (REST API) timeout. API Gateway is fundamentally designed for request/response cycles. If your SSE streams are truly long-lived (minutes or hours), API Gateway remains a poor fit. For such cases, consider alternatives like AWS App Runner, ECS Fargate, or EC2 instances behind an ALB, which are designed for long-lived connections.
2.  **Lambda Execution Duration:** While Function URLs support streaming, Lambda functions themselves still have an execution duration limit (up to 15 minutes). Design your SSE streams to fit within this constraint, or implement client-side reconnection logic using the `retry` field to gracefully re-establish the stream if the Lambda function completes or restarts.
3.  **Client Disconnect Detection:** The `CancellationToken` injected via `HttpContext` is crucial. It signals when the client has disconnected, allowing your Lambda to stop processing and clean up resources, avoiding unnecessary compute cycles.
4.  **Error Handling and Retries:** Always include the `retry:` field in your SSE messages. This tells the `EventSource` client how long to wait before attempting to reconnect after a disconnect or error. Combine this with robust server-side logging for diagnostics.
5.  **Scalability:** Each SSE connection is a long-lived HTTP connection, consuming a Lambda invocation. While Lambda scales wonderfully horizontally, a large number of concurrent, long-lived SSE connections can consume your concurrency limits. Monitor your `ConcurrentExecutions` metric closely. Consider strategies like broadcasting events via an intermediary service (e.g., EventBridge, SQS) to multiple Lambda functions if each connection requires substantial processing. For truly massive fan-out, WebSockets (with API Gateway's WebSocket API) or specialized services might be more appropriate.
6.  **CORS Headers:** Ensure correct CORS headers are sent (`Access-Control-Allow-Origin`, etc.) if your client is on a different domain. For SSE, often a wildcard `*` is sufficient during development, but tighten it in production.
7.  **Authentication and Authorization:** Securing your SSE stream is paramount. For Lambda Function URLs, you can use IAM authentication. If using an ALB, you can leverage ALB's built-in authentication features.

### Conclusion

Server-Sent Events provide an elegant, HTTP-native mechanism for one-way real-time data streaming that perfectly suits many application needs. While the traditional API Gateway -> Lambda proxy model is unsuitable for true SSE streaming, the introduction of Lambda Function URLs with `RESPONSE_STREAM` invocation mode unlocks the full potential of serverless SSE in .NET. By leveraging modern C# features like `IAsyncEnumerable` and carefully managing HTTP stream flushing, we can build efficient and responsive push-notification systems, bringing dynamic, real-time experiences to our users without the complexity overhead of full WebSockets, provided we choose the right serverless primitives.
