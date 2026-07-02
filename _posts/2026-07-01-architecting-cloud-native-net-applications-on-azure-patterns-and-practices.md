---
layout: post
title: "Architecting Cloud-Native .NET Applications on Azure: Patterns and Practices"
date: 2026-07-01 04:36:32 +0000
categories: dotnet blog
canonical_url: "https://dev.to/hossein_esmati/service-communication-patterns-in-net-core-and-azure-nhi"
---

We've all been there: staring at a distributed system's call graph, debugging a cascading failure that started with a simple HTTP request timing out. The promise of microservices – independent deployments, clear ownership, bounded contexts – often collides head-on with the realities of inter-service communication and the seductive convenience of shared code. Architecting cloud-native .NET applications on Azure forces us to confront these tensions head-on, particularly when it comes to how services talk to each other and what, if anything, they truly share.

The ease of spinning up new services in Azure (App Services, AKS, Container Apps, Functions) has amplified the need for robust communication strategies. Just throwing HTTP requests around might work for a handful of services, but as the system scales and failure domains proliferate, that synchronous dance quickly becomes a distributed monolith.

### The Nuance of Service Communication: Beyond Request-Response

For many, the default mental model for service communication is a direct HTTP request: `ServiceA` calls `ServiceB`, waits for a response, and continues. While perfectly valid for certain scenarios, particularly within a tight boundary or for user-initiated, synchronous flows, this pattern introduces significant coupling. `ServiceA` becomes dependent on `ServiceB`'s availability and latency. In a cloud-native environment, where services can scale independently, be updated asynchronously, or even fail temporarily, this tight coupling is a liability.

This is where asynchronous messaging patterns, often facilitated by robust brokers like Azure Service Bus or Azure Event Hubs, prove invaluable. Instead of direct calls, `ServiceA` publishes an event or sends a command, and `ServiceB` (or `C`, or `D`) subscribes and processes it independently.

Consider a scenario in an e-commerce platform: a `Catalog Service` needs to notify the `Inventory Service` and `Search Indexer` when a product's details are updated.

**Synchronous (Avoid for cross-cutting concerns):**
`Catalog Service` -> (HTTP POST) `Inventory Service`
`Catalog Service` -> (HTTP PUT) `Search Indexer`

This introduces three failure points and latency dependencies for a single product update operation. If `Search Indexer` is down, the product update in `Catalog` might fail, or worse, `Catalog` might complete successfully but the search index becomes stale.

**Asynchronous (Preferred for decoupled flows):**
`Catalog Service` -> (Publish Event) `ProductUpdatedEvent` to Azure Service Bus Topic
`Inventory Service` -> (Subscribe) `ProductUpdatedEvent` from Topic
`Search Indexer` -> (Subscribe) `ProductUpdatedEvent` from Topic

Here, the `Catalog Service`'s responsibility ends once the event is successfully published to the reliable message broker. It doesn't care if `Inventory` or `Search Indexer` are online at that exact moment. The broker ensures delivery, and consumers process messages at their own pace, leading to a much more resilient and scalable system. This embracing of eventual consistency is fundamental to cloud-native success.

### Managing Shared Libraries: A Double-Edged Sword

The flip side of service communication is what gets shared between services. The natural inclination is often to create a "Common" library containing DTOs, interfaces, utility functions, and even shared business logic. This path, while seemingly efficient in the short term, frequently leads to a distributed monolith, negating many benefits of microservices.

If `ServiceA` and `ServiceB` both depend on `SharedBusinessLogic.dll`, any change in that library necessitates redeploying both services. Versioning becomes a nightmare, and true independent deployment becomes impossible.

My rule of thumb is simple: **share only contracts and foundational infrastructure plumbing, never business logic or domain models.**

**What to share:**
*   **Message/Event Contracts:** DTOs representing commands, events, or query results. These are the language of communication between services.
*   **Client Interfaces (if using RPC):** `ICatalogService` and corresponding DTOs for gRPC or HTTP API clients.
*   **Common Infrastructure Concerns:** Highly generic logging abstractions, metric client interfaces, authentication token structures, or specific `HttpClient` configuration helpers that are truly universal.

**What *not* to share:**
*   **Domain Models:** Each service should own its internal domain model. A `Product` in the `Catalog Service` might have different properties and behaviors than a `Product` in the `Inventory Service`. Mapping between these bounded contexts is crucial.
*   **Business Logic:** This must reside within the service that owns that particular responsibility.
*   **Database Schemas/ORM Entities:** Direct sharing creates tight coupling to a data store and specific data structure, hindering independent evolution.

When defining shared contracts, use dedicated NuGet packages. Version them meticulously. A change in a contract (e.g., adding a new field to an event) should be a non-breaking change, ideally additive. Breaking changes require careful coordination and often a version bump, deploying consumers before producers, or vice-versa, depending on the change.

### A Deeper Dive: Implementing an Asynchronous Consumer on Azure

Let's illustrate a robust, cloud-native approach to consuming messages using .NET's `IHostedService` and Azure Service Bus. This pattern embodies resilience, proper resource management, and leverages modern .NET features.

Here's an example of a background service consuming `ProductUpdatedEvent` messages from an Azure Service Bus queue, demonstrating dependency injection, graceful shutdown, and structured logging.

```csharp
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Shared Contracts Library (e.g., MyCompany.Contracts.Catalog)
namespace MyCompany.Contracts.Catalog
{
    public record ProductUpdatedEvent(Guid ProductId, string Sku, string Name, decimal Price, DateTimeOffset Timestamp);
}

// Application specific configuration
public class ServiceBusConsumerSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
}

// The background worker hosted service
public class ProductUpdateConsumerService : BackgroundService, IAsyncDisposable
{
    private readonly ILogger<ProductUpdateConsumerService> _logger;
    private readonly ServiceBusClient _serviceBusClient;
    private ServiceBusProcessor _processor;
    private readonly string _queueName;

    // Injected dependencies: Logger, ServiceBusClient, Configuration
    public ProductUpdateConsumerService(
        ILogger<ProductUpdateConsumerService> logger,
        ServiceBusClient serviceBusClient, // Shared ServiceBusClient for connection management
        IOptions<ServiceBusConsumerSettings> settings)
    {
        _logger = logger;
        _serviceBusClient = serviceBusClient;
        _queueName = settings.Value.QueueName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProductUpdateConsumerService starting...");

        // Create a processor that we can use to process the messages
        // This is a robust client that handles concurrency, retries, and checkpointing
        _processor = _serviceBusClient.CreateProcessor(_queueName, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, // We'll manually complete messages
            MaxConcurrentCalls = 5,     // Process up to 5 messages concurrently
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10) // Keep lock alive for long-running operations
        });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        // Start processing messages
        await _processor.StartProcessingAsync(stoppingToken);

        _logger.LogInformation("ProductUpdateConsumerService processing messages. Press Ctrl+C to stop.");

        // Wait until the app is shut down
        await Task.Delay(Timeout.Infinite, stoppingToken);

        _logger.LogInformation("ProductUpdateConsumerService stopping.");
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        string body = args.Message.Body.ToString();
        _logger.LogInformation("Received message: {messageId} from queue: {queueName}", args.Message.MessageId, _queueName);

        try
        {
            // Deserialize the event contract
            var productUpdateEvent = JsonSerializer.Deserialize<MyCompany.Contracts.Catalog.ProductUpdatedEvent>(body);

            if (productUpdateEvent == null)
            {
                _logger.LogWarning("Could not deserialize message body into ProductUpdatedEvent. MessageId: {MessageId}", args.Message.MessageId);
                await args.DeadLetterMessageAsync(args.Message.LockToken, "Invalid message format");
                return;
            }

            // --- Business Logic for handling ProductUpdatedEvent ---
            // Example: Update inventory, invalidate cache, log the event
            _logger.LogInformation("Processing ProductUpdate for ProductId: {ProductId}, Sku: {Sku}", productUpdateEvent.ProductId, productUpdateEvent.Sku);

            // Simulate some work
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Complete the message after successful processing
            await args.CompleteMessageAsync(args.Message.LockToken);
            _logger.LogInformation("Successfully processed and completed message: {messageId}", args.Message.MessageId);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization error for messageId: {messageId}", args.Message.MessageId);
            // Dead-letter messages that cannot be deserialized to prevent reprocessing errors
            await args.DeadLetterMessageAsync(args.Message.LockToken, "JSON Deserialization Failed", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing messageId: {messageId}", args.Message.MessageId);
            // Nack the message to return it to the queue for retry based on Service Bus policy
            // In Service Bus, leaving the message in-flight and letting the lock expire will cause it to be retried
            // unless auto-completion is enabled. With AutoCompleteMessages=false, we rely on the lock timeout.
            // For explicit abandonment, args.AbandonMessageAsync() can be used.
            throw; // Re-throw to indicate failure, allowing the Service Bus SDK to handle retries based on policies
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Error occurred in processor for source: {source}, entity: {entity}, operation: {operation}",
            args.FullyQualifiedNamespace, args.EntityPath, args.ErrorSource.ToString());
        return Task.CompletedTask;
    }

    // IAsyncDisposable for graceful shutdown of Service Bus client components
    public async ValueTask DisposeAsync()
    {
        if (_processor != null)
        {
            // Stop processing messages and dispose the processor
            await _processor.StopProcessingAsync();
            await _processor.DisposeAsync();
        }
        // The _serviceBusClient is generally shared and disposed by the DI container on shutdown.
        // If this service had its own client, it would be disposed here.
    }
}

// Program.cs or Startup.cs configuration
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configuration from appsettings.json or environment variables
        builder.Services.Configure<ServiceBusConsumerSettings>(
            builder.Configuration.GetSection("ServiceBusConsumer"));

        // Register the shared ServiceBusClient as a singleton for efficiency
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<ServiceBusConsumerSettings>>().Value;
            return new ServiceBusClient(settings.ConnectionString);
        });

        // Register our hosted service
        builder.Services.AddHostedService<ProductUpdateConsumerService>();

        var host = builder.Build();
        await host.RunAsync();
    }
}
```

This code snippet exemplifies a few critical architectural choices for cloud-native .NET:

1.  **`IHostedService` (`BackgroundService`):** This is the idiomatic way in modern .NET to run long-running background tasks. It integrates seamlessly with the application's lifecycle, allowing for graceful startup and shutdown. This is crucial for cloud deployments where instances can be recycled at any time.
2.  **`Azure.Messaging.ServiceBus` Client Library:** The `ServiceBusClient` and `ServiceBusProcessor` are highly optimized for performance and resilience. The `ServiceBusProcessor` handles concurrent message processing, automatic lock renewal, and retry policies internally, significantly simplifying the developer's work.
3.  **Dependency Injection (DI):** The `ProductUpdateConsumerService` depends on `ILogger`, `ServiceBusClient`, and `IOptions<ServiceBusConsumerSettings>`. This makes the service testable, configurable, and allows for shared resources like the `ServiceBusClient` to be managed as singletons, reducing connection overhead.
4.  **Graceful Shutdown (`IAsyncDisposable`):** Implementing `IAsyncDisposable` (implicitly through `BackgroundService` if only stopping the processor) ensures that the message processor is stopped cleanly, preventing in-flight messages from being lost and allowing the underlying connections to be released properly when the application shuts down.
5.  **Explicit Message Completion/Dead-Lettering:** By setting `AutoCompleteMessages = false`, we take explicit control. Messages are only marked as processed (`CompleteMessageAsync`) after the application's business logic successfully completes. Failures are handled by throwing exceptions (allowing Service Bus to retry) or explicitly moving to the dead-letter queue (`DeadLetterMessageAsync`) for messages that are fundamentally malformed or unprocessable. This prevents poison-message scenarios.
6.  **Structured Logging:** Using `ILogger` with message templates and named parameters enables rich, queryable logs in systems like Azure Application Insights or Log Analytics, vital for debugging distributed systems.
7.  **Separation of Concerns for Contracts:** The `ProductUpdatedEvent` is defined in a distinct `MyCompany.Contracts.Catalog` namespace, implicitly indicating it would be in its own NuGet package. This ensures that only the contract definition, not the entire `Catalog Service`'s domain, is shared.

### Pitfalls and Best Practices

*   **Pitfall: Synchronous calls across service boundaries for non-critical reads.** If a service needs data from another for a non-real-time display, consider denormalizing the data or using a read replica. Every synchronous hop adds latency and a point of failure.
*   **Best Practice: Implement idempotency for message handlers.** Due to the "at-least-once" delivery guarantee of most message brokers, your message handlers must be able to process the same message multiple times without adverse side effects. Use a unique message ID (often found in `args.Message.MessageId` or a correlation ID within your message payload) to check if an operation has already been performed.
*   **Pitfall: Sharing business logic in common libraries.** This is the fastest way to couple services tightly. If two services genuinely need the same logic, it likely indicates that either: a) the logic belongs to a new, dedicated service, or b) the shared component is a generic utility, not domain-specific.
*   **Best Practice: Design for failure.** Assume network partitions, service outages, and transient errors. Implement retries with exponential backoff (the Service Bus client does a good job here), circuit breakers (e.g., Polly), and comprehensive monitoring.
*   **Pitfall: Ignoring message size limits or throughput quotas.** Azure Service Bus has limits. Large messages or high volumes without proper partitioning can lead to throttling. Design your events to be lean. If large payloads are needed, consider storing the data in Blob Storage and sending a message with a reference to the blob.
*   **Best Practice: Use DTOs for contracts, not direct entity models.** Your internal database entities should be distinct from the public-facing DTOs or event contracts. This allows your internal data model to evolve independently without breaking consumers.

Building robust cloud-native .NET applications on Azure is a continuous exercise in conscious decoupling. It means embracing asynchronous patterns, carefully managing shared contracts, and designing with resilience as a first-class citizen. The journey requires a shift in mindset from monolithic assumptions to distributed realities, leveraging modern .NET features and Azure's powerful platform capabilities to build systems that truly scale and adapt.
