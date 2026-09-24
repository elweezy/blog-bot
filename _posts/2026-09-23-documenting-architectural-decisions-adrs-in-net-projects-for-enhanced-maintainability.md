---
layout: post
title: "Documenting Architectural Decisions (ADRs) in .NET Projects for Enhanced Maintainability"
date: 2026-09-23 08:22:27 +0000
categories: dotnet blog
canonical_url: "https://stackoverflow.com/questions/1109558/allocating-more-than-1-000-mb-of-memory-in-32-bit-net-process"
---

The call came late on a Friday afternoon. A production issue. A critical backend service, designed years ago, was intermittently failing under what seemed like moderate load. The stack traces were perplexing, pointing to a data access pattern that, on the surface, felt wildly inefficient for a high-throughput system. Diving into the codebase, the team and I were met with a wall of uncommented, highly optimized (or so it seemed) SQL queries executed in a bizarre sequence. There were hints of an optimistic concurrency strategy, but without any explicit documentation explaining *why* this complex dance of reads, updates, and retries was chosen over a more straightforward, transactional approach, debugging was a nightmare. The original architect? Long gone. The project wiki? Last updated before the current framework version existed. This wasn't just a bug; it was an archeological expedition into forgotten intent.

This scenario, regrettably, is far too common. Modern .NET ecosystems, with their lean microservices, event-driven architectures, and rapid deployment cycles, amplify the complexity of understanding *why* systems are built the way they are. Teams are fluid, requirements shift, and technology evolves at a dizzying pace. Without a clear, accessible record of the fundamental choices made during a system's evolution, every new developer joins an archeological dig, and every significant change risks introducing regressions or violating unspoken architectural principles. This is precisely where Architectural Decision Records (ADRs) earn their keep.

ADRs aren't a new concept, but their relevance has exploded with the increasing distribution and modularity of .NET applications. They are lightweight documents that capture a single architectural decision, its context, the options considered, the rationale behind the chosen solution, and its consequences. They serve as a shared, living memory of your system's design journey, making explicit the implicit knowledge often lost in chat logs, meeting minutes, or the minds of departed team members.

### The ADR: A Blueprint for Clarity

An ADR typically follows a simple, consistent structure. While formats can vary, a common pattern looks like this:

*   **Title:** A concise, descriptive name (e.g., "Use RabbitMQ for Asynchronous Message Processing").
*   **Status:** Proposed, Accepted, Rejected, Superseded.
*   **Context:** The problem or dilemma being addressed. What forces are at play? What constraints exist?
*   **Decision:** The chosen solution. A clear statement of what was decided.
*   **Consequences:** The implications, both positive and negative, of the decision. What does this enable? What does it prevent? What are the trade-offs in terms of performance, maintainability, cost, or operational complexity?

The power of ADRs lies in their brevity and focus on *why*. They aren't extensive design documents; they are snapshots of critical crossroads. Storing them as Markdown files within your version control system, close to the code they describe, makes them discoverable and versioned alongside the implementation itself.

### ADRs in Action: Decoupling Internal Command Dispatch

Consider a common scenario in a moderately complex .NET microservice: internal command dispatch. You have various components within the same service that need to send messages (commands) to other internal components for processing. For instance, an API endpoint might receive a request, validate it, and then "dispatch" a command to a background processor to perform the actual work.

The architectural decision here might be: how do we facilitate this internal communication?

**Options considered:**

1.  **Direct Method Calls:** Simple, synchronous, tightly coupled.
2.  **In-Memory Event Bus (e.g., MediatR):** Decouples handlers from senders, but still synchronous by default, and can become complex for asynchronous workflows.
3.  **External Message Broker (e.g., RabbitMQ, Kafka):** Offers robust asynchronous processing, persistence, scalability, but introduces operational overhead, network latency, and requires external infrastructure.
4.  **In-Process Channel<T> with BackgroundService:** Asynchronous processing within the same service process, providing a non-blocking internal queue without external dependencies.

Let's assume the ADR documented the decision to use an in-process `Channel<T>` with a `BackgroundService` for specific internal command processing, citing performance, reduced operational complexity, and the fact that distributed guarantees were *not* needed for *these specific internal commands*.

Here's how that architectural decision might manifest in production-grade C# code:

```csharp
// ADR-005: Use In-Process Channel for Internal Command Dispatch
// Status: Accepted
// Context: We need to asynchronously process internal commands (e.g., email notifications, data enrichment tasks) 
// within a single microservice without introducing external message broker dependencies for these specific concerns. 
// These commands do not require distributed guarantees or persistence across service restarts.
// Decision: We will use a System.Threading.Channels.Channel<T> as an in-memory queue, consumed by an IHostedService, 
// for internal command dispatch. This provides asynchronous, non-blocking internal messaging.
// Consequences: 
//   Positive: High performance, low latency, reduced operational overhead, simplified deployment.
//   Negative: Commands are not persisted across service restarts (volatile queue), 
//             limited to single-instance processing (not horizontally scalable for *these* commands).
//             Requires careful error handling within the consumer to prevent message loss.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

// Define a simple command interface and a concrete command
public interface IInternalCommand { }

public record ProcessDataCommand(Guid DataId, string Payload) : IInternalCommand;

// Service to publish commands
public class InternalCommandPublisher
{
    private readonly ChannelWriter<IInternalCommand> _writer;
    private readonly ILogger<InternalCommandPublisher> _logger;

    public InternalCommandPublisher(Channel<IInternalCommand> channel, ILogger<InternalCommandPublisher> logger)
    {
        _writer = channel.Writer;
        _logger = logger;
    }

    public async Task PublishAsync(IInternalCommand command, CancellationToken cancellationToken = default)
    {
        await _writer.WriteAsync(command, cancellationToken);
        _logger.LogInformation("Published internal command {CommandType} for {DataId}", 
            command.GetType().Name, (command as ProcessDataCommand)?.DataId);
    }
}

// Background service to consume and process commands
public class InternalCommandProcessorService : BackgroundService
{
    private readonly ChannelReader<IInternalCommand> _reader;
    private readonly ILogger<InternalCommandProcessorService> _logger;
    private readonly IServiceProvider _serviceProvider; // To resolve handlers via DI scope

    public InternalCommandProcessorService(
        Channel<IInternalCommand> channel, 
        ILogger<InternalCommandProcessorService> logger,
        IServiceProvider serviceProvider)
    {
        _reader = channel.Reader;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InternalCommandProcessorService started.");

        await foreach (var command in _reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                _logger.LogDebug("Processing internal command {CommandType}", command.GetType().Name);
                
                // In a real application, you'd dispatch this command to a specific handler.
                // For demonstration, we'll simulate processing based on command type.
                await ProcessCommandAsync(command, stoppingToken);

                _logger.LogInformation("Successfully processed internal command {CommandType}", command.GetType().Name);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during shutdown
                _logger.LogWarning("InternalCommandProcessorService is shutting down.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing internal command {CommandType}. Message will be lost.", command.GetType().Name);
                // Depending on the command, you might want a retry mechanism, dead-letter queue, or specific error handling here.
                // Given the ADR's context (no distributed guarantees), simple logging and proceeding might be acceptable.
            }
        }

        _logger.LogInformation("InternalCommandProcessorService stopped.");
    }

    private async Task ProcessCommandAsync(IInternalCommand command, CancellationToken cancellationToken)
    {
        // Simulate actual work, potentially dispatching to a handler via DI
        // using (var scope = _serviceProvider.CreateScope())
        // {
        //     var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand>>();
        //     await handler.HandleAsync((TCommand)command, cancellationToken);
        // }

        switch (command)
        {
            case ProcessDataCommand dataCommand:
                _logger.LogInformation("Processing data for {DataId} with payload '{Payload}'", dataCommand.DataId, dataCommand.Payload);
                await Task.Delay(100, cancellationToken); // Simulate async work
                break;
            default:
                _logger.LogWarning("No handler found for command type {CommandType}", command.GetType().Name);
                break;
        }
    }
}

// Minimal API setup for demonstration
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Configure the Channel for DI
builder.Services.AddSingleton(Channel.CreateUnbounded<IInternalCommand>(new UnboundedChannelOptions
{
    SingleReader = false, // Allow multiple readers if needed for future scaling within the process
    SingleWriter = false, // Allow multiple writers
    AllowSynchronousContinuations = false // Avoid blocking threads
}));
builder.Services.AddSingleton<InternalCommandPublisher>();
builder.Services.AddHostedService<InternalCommandProcessorService>();
builder.Services.AddLogging(config => config.AddConsole()); // For demonstration

var app = builder.Build();

app.MapPost("/dispatch-data", async (InternalCommandPublisher publisher, ProcessDataCommand command) =>
{
    await publisher.PublishAsync(command);
    return Results.Accepted();
});

app.Run();
```

The code directly reflects the decision in the ADR. We're using `System.Threading.Channels`, specifically an unbounded channel, because the ADR explicitly states that distributed guarantees are *not* needed and the goal is high performance within a single process. The `BackgroundService` ensures that the processing is robustly integrated into the .NET host lifecycle, handling startup and graceful shutdown. Dependency Injection is used to provide the channel and services, maintaining loose coupling. Structured logging is present to give operational insights.

Crucially, the code snippet has the ADR *as a comment block* right above it. While I wouldn't recommend this for all ADRs (a separate Markdown file is usually better), it illustrates the direct link between decision and implementation. The ADR helps a future developer understand *why* `Channel.CreateUnbounded` was chosen over, say, a bounded channel (perhaps to avoid backpressure for these specific, non-critical commands) or why an external broker wasn't used despite its prevalence. It highlights the trade-offs: fast and simple, but without persistence. This context is invaluable when debugging, refactoring, or scaling.

### Pitfalls and Best Practices

1.  **Over-documentation vs. Under-documentation:** The primary pitfall is either documenting every minor decision (leading to ADR fatigue and stale documents) or documenting nothing at all. ADRs should focus on *significant* architectural decisions – those with non-trivial trade-offs, impacting multiple components, or setting key constraints. "Should we use `new()` or `var`?" is not an ADR. "Should we adopt event sourcing for our domain model?" absolutely is.
2.  **Stale ADRs:** An ADR is a living document. If a decision is revisited or superseded by new technology or requirements, the ADR's status must be updated. A `Superseded` status, linking to the new ADR, maintains a clear historical trail. Regular reviews during architectural spikes or refactoring efforts can help keep them current.
3.  **ADRs as Law:** While ADRs capture decisions, they should not be treated as immutable laws. They are records of *past* decisions based on *past* contexts. They invite discussion and challenge when the context changes, preventing adherence to outdated patterns simply because "it was decided."
4.  **Integration into Workflow:** ADRs are most effective when they are a natural part of your development workflow. When a significant architectural discussion occurs, the outcome should ideally be an ADR. Tools and templates can streamline their creation. Integrating them into pull requests for architectural changes ensures team consensus and review.
5.  **Conciseness:** Keep ADRs focused and to the point. The goal is clarity and context, not a thesis. Use concise language and avoid jargon where simpler terms suffice.

Architectural Decision Records are more than just documentation; they are a form of intellectual property capture. They codify the reasoning that shapes our systems, allowing teams to learn from past choices, onboard new members efficiently, and navigate the inevitable evolution of software with greater confidence and less guesswork. Embracing ADRs is an act of engineering maturity, a tangible investment in the long-term maintainability and health of your .NET projects.
