---
layout: post
title: "Integrating Contextual AI and LLMs in .NET Applications"
date: 2025-12-10 03:34:38 +0000
categories: dotnet blog
canonical_url: "https://stackoverflow.com/questions/77322704/load-clientid-and-clientsecret-from-the-database-in-net"
---

We've spent decades perfecting deterministic logic within our applications: strict business rules, precise data transformations, and predictable workflows. Yet, a growing class of problems resists this approach. Think about the common challenge of making sense of unstructured customer feedback, generating contextually relevant responses in a support chatbot, or automatically summarizing complex documents. These aren't just data processing tasks; they demand *understanding* and inference, capabilities that traditional algorithmic logic struggles to provide.

The recent maturation of large language models (LLMs) and the availability of sophisticated contextual AI services have fundamentally shifted what's possible within our .NET applications. We're no longer just orchestrating data; we're embedding a layer of probabilistic reasoning, allowing our software to comprehend, generate, and adapt in ways that were once confined to science fiction. This isn't about replacing engineers with AI; it's about equipping our systems with new tools to tackle intelligence-heavy workloads, freeing up human expertise for higher-value activities.

### The New Architecture: Integrating External Intelligence

Integrating LLMs into a .NET application primarily revolves around consuming external APIs. While some niche scenarios might justify running smaller, specialized models locally (e.g., using ONNX Runtime with pre-trained models for specific tasks), the vast majority of production-grade LLM use cases leverage cloud services like Azure OpenAI, OpenAI's API, or Google Gemini. This outsourcing of the model inference allows us to focus on data orchestration, prompt engineering, and result interpretation, rather than the complexities of model deployment and scaling.

The core challenge isn't merely making an HTTP call. It's about designing a resilient, cost-effective, and context-aware integration layer. We need to consider:

1.  **Context Management:** LLMs have finite "context windows." How do we feed them enough information to be useful without exceeding limits or incurring excessive costs? This often involves pre-processing input, employing strategies like Retrieval Augmented Generation (RAG) by fetching relevant data from vector stores or databases, and managing conversation history.
2.  **Prompt Engineering:** The quality of the LLM's output is directly tied to the clarity and effectiveness of the input prompt. This is less about coding and more about crafting precise instructions, providing examples (few-shot prompting), and defining desired output formats (e.g., JSON schema).
3.  **Asynchronous and Streaming Interactions:** LLM calls can be slow and often return results incrementally. Our .NET applications need to be built to handle this asynchronously and take advantage of streaming APIs where available to improve user experience and resource utilization.
4.  **Cost and Performance Optimization:** Each token processed by an LLM incurs a cost. Careful prompt design, caching strategies, and intelligent context truncation become critical for managing operational expenses.

### Crafting a Contextual Service Layer

Let's consider a practical example: building an internal tool that summarizes meeting transcripts and extracts key action items. This involves sending potentially large text blocks to an LLM and parsing its response. We want to do this efficiently, asynchronously, and robustly.

Here's how we might structure a service using modern .NET practices, focusing on dependency injection, asynchronous streaming, and configuration management.

```csharp
// Program.cs - Minimal API setup and Dependency Injection
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices; // For [EnumeratorCancellation]

var builder = WebApplication.CreateBuilder(args);

// Configure logging
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Configure HttpClient
builder.Services.AddHttpClient<ILLMService, OpenAIProxyService>(client =>
{
    // A production scenario would use named clients or a more robust
    // approach for different LLM providers/endpoints.
    client.BaseAddress = new Uri(builder.Configuration["OpenAI:BaseUrl"] ?? "https://api.openai.com/");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["OpenAI:ApiKey"]}");
});

// Bind LLM specific configuration
builder.Services.Configure<OpenAIConfig>(builder.Configuration.GetSection("OpenAI"));

// Register the LLM service
builder.Services.AddScoped<ILLMService, OpenAIProxyService>();

var app = builder.Build();

app.UseHttpsRedirection();

// Minimal API endpoint for summarizing transcripts
app.MapPost("/summarize", async (
    SummarizeRequest request,
    ILLMService llmService,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Transcript))
    {
        return Results.BadRequest("Transcript cannot be empty.");
    }

    logger.LogInformation("Received summarization request. Transcript length: {Length}", request.Transcript.Length);

    try
    {
        var summaryStream = llmService.StreamSummaryAsync(request.Transcript, cancellationToken);
        
        // Example: Stream the summary back chunk by chunk
        // In a real application, you might process these chunks
        // or aggregate them before returning.
        return Results.Stream(async (stream) =>
        {
            var writer = new StreamWriter(stream);
            await foreach (var chunk in summaryStream.WithCancellation(cancellationToken))
            {
                await writer.WriteAsync(chunk);
                await writer.FlushAsync();
            }
        }, "text/plain");
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Summarization request cancelled.");
        return Results.Problem("Summarization request cancelled.", statusCode: 499); // Client Closed Request
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error during summarization.");
        return Results.Problem("An unexpected error occurred during summarization.", statusCode: 500);
    }
});

app.Run();

// Configuration object for OpenAI settings
public class OpenAIConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/";
    public string Model { get; set; } = "gpt-4o-mini"; // Use a cost-effective model for many tasks
    public double Temperature { get; set; } = 0.7;
}

// Request DTO
public record SummarizeRequest(string Transcript);

// LLM Service Interface
public interface ILLMService
{
    IAsyncEnumerable<string> StreamSummaryAsync(string transcript, CancellationToken cancellationToken);
}

// OpenAI implementation of the LLM Service
public class OpenAIProxyService : ILLMService
{
    private readonly HttpClient _httpClient;
    private readonly OpenAIConfig _config;
    private readonly ILogger<OpenAIProxyService> _logger;

    public OpenAIProxyService(HttpClient httpClient, IOptions<OpenAIConfig> config, ILogger<OpenAIProxyService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure API Key is not empty
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _logger.LogCritical("OpenAI API Key is not configured. Please check your appsettings.json or environment variables.");
            throw new InvalidOperationException("OpenAI API Key is missing.");
        }
    }

    public async IAsyncEnumerable<string> StreamSummaryAsync(string transcript, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Crafting the prompt is critical. This is a basic example.
        // In reality, this prompt might be loaded from a configuration, external file,
        // or a dedicated prompt engineering service.
        var prompt = $@"
            You are an expert meeting summarizer. Your task is to concisely summarize the following meeting transcript
            and then list any explicit action items with their responsible parties.
            Format your output as follows:

            Summary:
            [Concise summary of the meeting]

            Action Items:
            - [Action Item 1] (Responsible Party)
            - [Action Item 2] (Responsible Party)

            Meeting Transcript:
            ""{transcript}""
        ";

        var requestBody = new
        {
            model = _config.Model,
            messages = new[]
            {
                new { role = "system", content = "You are a helpful assistant." },
                new { role = "user", content = prompt }
            },
            temperature = _config.Temperature,
            stream = true // Request streaming response
        };

        try
        {
            // The request URI path depends on the specific LLM API (e.g., /v1/chat/completions for OpenAI)
            var response = await _httpClient.PostAsJsonAsync("v1/chat/completions", requestBody, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Read the streaming response from the LLM
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(responseStream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // OpenAI streaming responses are Server-Sent Events (SSE) format,
                // each line prefixed with "data: ".
                if (line.StartsWith("data: "))
                {
                    var jsonData = line.Substring("data: ".Length);
                    if (jsonData == "[DONE]") break; // End of stream marker

                    var completionChunk = JsonSerializer.Deserialize<OpenAICompletionChunk>(jsonData);
                    var content = completionChunk?.choices?.FirstOrDefault()?.delta?.content;

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        yield return content;
                    }
                }
            }
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP request to OpenAI failed. Status: {StatusCode}, Content: {Content}", 
                             httpEx.StatusCode, httpEx.Message);
            throw;
        }
        catch (JsonException jsonEx)
        {
            _logger.LogError(jsonEx, "Failed to deserialize OpenAI streaming response.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OpenAI streaming request cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred while interacting with OpenAI.");
            throw;
        }
    }
}

// DTOs for deserializing OpenAI streaming responses
// These are simplified for brevity and focus on 'content'
public class OpenAICompletionChunk
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public long Created { get; set; }
    public string Model { get; set; } = string.Empty;
    public List<Choice> Choices { get; set; } = new();
}

public class Choice
{
    public int Index { get; set; }
    public Delta Delta { get; set; } = new();
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class Delta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}
```

### Deconstructing the Example: Rationale and Trade-offs

*   **Dependency Injection & `HttpClient`:** We're leveraging .NET's `HttpClientFactory` via `AddHttpClient`. This is the established best practice for managing `HttpClient` instances, preventing common issues like socket exhaustion while allowing configuration via DI. The API key is sourced from `IConfiguration`, which itself should be backed by secure sources (e.g., Azure Key Vault, environment variables, or encrypted `appsettings.json`) in production, *never* hardcoded.
*   **`IOptions<OpenAIConfig>`:** This pattern binds a specific section of the configuration (e.g., `"OpenAI"`) to a strongly typed class. It promotes compile-time safety and makes configuration easier to manage than direct string lookups.
*   **`IAsyncEnumerable<string>`:** This is a cornerstone for efficient LLM integration. Instead of waiting for the entire LLM response (which can take several seconds for longer outputs), `IAsyncEnumerable` allows us to yield chunks of the response as they arrive.
    *   **Performance:** Reduces perceived latency for the user, as parts of the answer can be displayed immediately.
    *   **Resource Management:** Minimizes memory usage since the entire response doesn't need to be buffered.
    *   **Error Handling:** Enables partial processing or early cancellation if an issue arises mid-stream. The `[EnumeratorCancellation]` attribute on the `CancellationToken` parameter is crucial for cooperative cancellation within `IAsyncEnumerable` methods.
*   **`Minimal API`:** The `app.MapPost` demonstrates a concise way to expose an endpoint in modern .NET. It ties together the request DTO, service injection, and streaming response with minimal boilerplate. The `Results.Stream` method directly pipes the `IAsyncEnumerable` to the HTTP response, showcasing a powerful pattern for streaming data.
*   **Error Handling and Logging:** Robust `try-catch` blocks are essential, especially when dealing with external services. We log critical issues, HTTP errors, and JSON deserialization failures. `OperationCanceledException` is handled specifically, reflecting user cancellations or timeouts, which are common in streaming scenarios.
*   **Prompt Engineering in Code:** The `prompt` string is crucial. In a real-world system, this would be far more sophisticated, perhaps dynamically generated based on user input or retrieved from a prompt template management system. The `temperature` setting directly influences the creativity vs. determinism of the LLM output.
*   **Response Deserialization:** Parsing the SSE (Server-Sent Events) format that many LLMs use for streaming is a common pattern. We deserialize each "data" line into a specific DTO (`OpenAICompletionChunk`), extracting the `content` chunk.

### Pitfalls and Best Practices

1.  **Cost Management is Paramount:** LLM API usage is metered by tokens.
    *   **Pitfall:** Sending entire documents or verbose prompts without optimization.
    *   **Best Practice:** Implement prompt compression (e.g., few-shot examples or instructions), summarization before sending to the LLM, and careful context window management (e.g., only include the *most relevant* prior turns in a chat history). Cache results for common queries.
2.  **Latency and User Experience:** LLM calls are not instantaneous.
    *   **Pitfall:** Synchronous calls or blocking the UI thread.
    *   **Best Practice:** Embrace `async`/`await` and `IAsyncEnumerable` for streaming. Design user interfaces to provide immediate feedback and display partial results as they arrive. Consider background processing for non-urgent tasks.
3.  **Data Privacy and Security:** Sending sensitive information to third-party LLMs.
    *   **Pitfall:** Uncritically sending PII or confidential business data.
    *   **Best Practice:** Anonymize or redact sensitive data before transmission. Understand the data retention and privacy policies of your chosen LLM provider. For highly sensitive data, explore private deployments (e.g., Azure OpenAI on your VNet) or local, self-hosted models if feasible.
4.  **LLM "Hallucinations" and Reliability:** LLMs can generate plausible but incorrect information.
    *   **Pitfall:** Blindly trusting LLM output as factual.
    *   **Best Practice:** Implement human-in-the-loop workflows for critical decisions. Add guardrails or validation layers where possible. For factual retrieval, pair the LLM with a RAG system that pulls from trusted knowledge bases.
5.  **Rate Limiting and Resilience:** External LLM services have usage quotas.
    *   **Pitfall:** Not handling `429 Too Many Requests` or other API errors gracefully.
    *   **Best Practice:** Implement retry logic with exponential backoff. Distribute requests across multiple API keys or instances if necessary. Monitor usage and set alerts. Consider circuit breakers to prevent overwhelming the LLM service during outages.
6.  **Prompt Versioning and Testing:** Prompts are effectively code and need to be managed.
    *   **Pitfall:** Treating prompts as ad-hoc strings.
    *   **Best Practice:** Externalize prompts, use version control for them, and implement automated prompt testing (evaluating LLM responses against expected outputs for a given set of inputs).

Integrating contextual AI and LLMs is more than just making an HTTP call; it's about embracing a new paradigm of probabilistic computing within our deterministic systems. It demands a thoughtful approach to architecture, robust error handling, diligent cost management, and a deep understanding of the LLM's capabilities and limitations. As .NET architects, we're now tasked with bridging the gap between predictable logic and intelligent inference, building applications that are not just efficient, but truly insightful.
