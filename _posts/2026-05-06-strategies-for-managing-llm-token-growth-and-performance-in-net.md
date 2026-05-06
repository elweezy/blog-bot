---
layout: post
title: "Strategies for Managing LLM Token Growth and Performance in .NET"
date: 2026-05-06 04:20:21 +0000
categories: dotnet blog
canonical_url: "https://dev.to/logicgriddev/a-semantic-kernel-alternative-for-net-when-and-why-youd-reach-for-one-40l"
---

Integrating Large Language Models into .NET applications opens up vast possibilities, yet it quickly surfaces a critical architectural concern: managing token consumption. We've all seen it – an application that works flawlessly in testing suddenly starts racking up unexpected API costs or exhibits painful latency in production. More often than not, the culprit isn't the LLM itself, but an unoptimized approach to prompt construction and context management, leading to token growth that's both insidious and expensive.

This isn't just about the dollar cost, although that's a significant factor. Every token transmitted to and from an LLM API contributes to network latency, model inference time, and API rate limit consumption. An application that's sluggish or frequently hits rate limits because it's sending excessively large prompts offers a poor user experience and scales poorly. In the modern cloud-native landscape, where every millisecond and every penny counts, token efficiency is no longer an optional optimization; it's a fundamental design principle for any serious LLM-powered system.

### Understanding the Token Landscape

Before we can manage tokens, we need to understand what they are. LLMs don't process words; they process tokens. A token is a sequence of characters that the model's tokenizer breaks text into. Sometimes a word is a token, sometimes it's multiple tokens ("un-believ-able"), and sometimes multiple words form a single token. Code, JSON, and special characters also consume tokens. The crucial takeaway is that the relationship between human-readable text and token count isn't always intuitive.

Token consumption isn't just about the initial user query. It's an aggregate of:

1.  **System Prompts:** The initial instructions that define the LLM's role, persona, and constraints.
2.  **User Inputs:** The actual query or conversation turn from the user.
3.  **Few-Shot Examples:** If you're providing examples of desired input/output behavior.
4.  **Retrieval Augmented Generation (RAG) Context:** External documents, database records, or other data injected to ground the LLM's response.
5.  **Function Calling / Tool Definitions:** Descriptions of functions or tools the LLM can invoke.
6.  **Conversational History:** Previous turns in a multi-turn dialogue.
7.  **Model Output:** The LLM's response also consumes tokens, often limited by an explicit `max_tokens` parameter.

Each of these vectors offers opportunities for optimization.

### Strategies for Taming Token Growth

Our goal is to deliver the necessary context to the LLM to achieve the desired outcome, and *no more*. This demands a deliberate, engineering-driven approach rather than a "dump everything in" mentality.

#### 1. Precision in Prompt Engineering

The first line of defense against token bloat is the system prompt itself.
*   **Be Concise, Not Curt:** Remove redundant phrases, unnecessary pleasantries, and overly verbose explanations. Every word should earn its place.
*   **Structured Prompts:** Leverage JSON or XML delimiters for complex instructions or input parameters. This often helps the model parse intent more efficiently, sometimes requiring fewer tokens than ambiguous natural language. For example, instead of "The user wants to find books by the author. Their name is John Doe," use something like:
    ```json
    {
      "action": "search_books",
      "criteria": {
        "author": "John Doe"
      }
    }
    ```
*   **Dynamic Prompting:** Don't send static, monolithic system prompts for every interaction. A "general assistant" prompt is fine, but if the user explicitly asks to "schedule a meeting," dynamically inject *only* the system instructions and function definitions relevant to scheduling. This can drastically reduce the average prompt size.

#### 2. Intelligent RAG Context Management

RAG is a powerful pattern, but it's also a major source of token overconsumption.
*   **Semantic Chunking:** Instead of naive fixed-size chunking, analyze the document structure and meaning to create chunks that are logically coherent. For instance, chunking by paragraph, section, or even entire code functions makes more sense than arbitrary character limits.
*   **Pre-filtering and Re-ranking:** Before injecting RAG documents into the main LLM prompt, perform an initial filtering pass. A smaller, cheaper embedding model can identify the top `N` most relevant chunks. If `N` is still too large, consider a second, potentially slightly more capable model (or a fast local model) to re-rank these `N` chunks and select only the truly essential `K` chunks.
*   **Context Summarization:** For very long, but highly relevant, retrieved documents, consider using a smaller, faster LLM to summarize them *before* injecting the summary into the main prompt. This is a token-for-token trade-off: you spend tokens on summarization to save a potentially much larger number of tokens in the primary LLM call. This often works well when the precise details of the original document aren't needed, only the core facts.

#### 3. Conversational History Pruning

Long-running conversations are notoriously expensive.
*   **Windowing:** The simplest approach is to maintain a fixed window of the last `N` turns. This is easy to implement but can lead to loss of crucial context from earlier in the conversation.
*   **Summarization:** Periodically summarize the conversation history. After, say, 5-10 turns, use an LLM to generate a concise summary of the conversation so far, then discard the older individual turns, keeping only the summary and the most recent `M` turns. This requires spending tokens on summarization but provides a much more compact representation of the dialogue's essence.
*   **Hybrid Approaches:** Combine windowing with summarization. Always keep the last `M` turns verbatim, and then include a summary of the *entire* conversation *prior* to those `M` turns.
*   **Entity Extraction:** For task-oriented bots, extract key entities (e.g., names, dates, product IDs) and store them in a structured session state. Then, when constructing the prompt, inject only the necessary entities rather than the full conversational history.

### A Token-Aware Prompt Builder in .NET

Let's illustrate some of these concepts with a `.NET` example. We'll build a `PromptContextBuilder` that intelligently assembles a prompt for a chat application, prioritizing system instructions, recent history, and then dynamically adding RAG context while staying within a configurable token budget. We'll use a simple character-based token estimator for demonstration, but in production, you'd integrate with the specific tokenizer of your chosen LLM (e.g., `tiktoken` via a C# port or direct API).

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// For simplicity, let's assume a basic message structure.
// In a real app, this might be from a specific LLM SDK or a domain model.
public record ChatMessage(string Role, string Content);

// Configuration for our token management
public class TokenManagementOptions
{
    public int MaxPromptTokens { get; set; } = 4096; // Example default
    public int MaxOutputTokens { get; set; } = 512;
    public int RecentHistoryTurnsToKeep { get; set; } = 5; // How many recent turns to keep verbatim
    public double TokenEstimateFactor { get; set; } = 4.0; // Characters per token (rough estimate)
}

public interface IPromptContextBuilder
{
    Task<(string Prompt, int EstimatedTokens)> BuildPromptAsync(
        string systemMessage,
        IEnumerable<ChatMessage> chatHistory,
        IEnumerable<string> ragContexts,
        CancellationToken cancellationToken = default);
}

public class PromptContextBuilder : IPromptContextBuilder
{
    private readonly ILogger<PromptContextBuilder> _logger;
    private readonly TokenManagementOptions _options;

    public PromptContextBuilder(
        ILogger<PromptContextBuilder> logger,
        IOptions<TokenManagementOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Estimates tokens based on a simple character-to-token ratio.
    /// In production, use a proper tokenizer for the specific LLM.
    /// </summary>
    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / _options.TokenEstimateFactor);
    }

    public async Task<(string Prompt, int EstimatedTokens)> BuildPromptAsync(
        string systemMessage,
        IEnumerable<ChatMessage> chatHistory,
        IEnumerable<string> ragContexts,
        CancellationToken cancellationToken = default)
    {
        var promptParts = new List<string>();
        int currentTokens = 0;

        // 1. Add System Message (highest priority)
        if (!string.IsNullOrEmpty(systemMessage))
        {
            promptParts.Add($"<system>{systemMessage}</system>");
            currentTokens += EstimateTokens(systemMessage) + EstimateTokens("<system></system>");
        }

        // Check if system message alone exceeds budget (unlikely but possible)
        if (currentTokens >= _options.MaxPromptTokens)
        {
            _logger.LogWarning("System message alone consumes {Tokens} tokens, exceeding max budget of {MaxTokens}.",
                currentTokens, _options.MaxPromptTokens);
            return (promptParts.First(), currentTokens); // Or throw, depending on policy
        }

        // 2. Add Recent Chat History (high priority, windowed)
        var recentHistory = chatHistory.Reverse().Take(_options.RecentHistoryTurnsToKeep).Reverse().ToList();
        var historyTokens = 0;
        var historyParts = new List<string>();

        foreach (var message in recentHistory)
        {
            var formattedMessage = $"<{message.Role}>{message.Content}</{message.Role}>";
            var messageTokenCount = EstimateTokens(formattedMessage);

            // If adding this message would exceed max prompt tokens, break
            if (currentTokens + historyTokens + messageTokenCount >= _options.MaxPromptTokens)
            {
                _logger.LogWarning("Skipping older chat history due to token budget. Current tokens: {CurrentTokens}, History tokens accumulated: {HistoryTokens}",
                    currentTokens, historyTokens);
                break;
            }

            historyParts.Add(formattedMessage);
            historyTokens += messageTokenCount;
        }

        promptParts.AddRange(historyParts);
        currentTokens += historyTokens;

        // 3. Add RAG Context (dynamic pruning)
        var ragTokens = 0;
        var ragParts = new List<string>();
        foreach (var context in ragContexts.OrderByDescending(c => c.Length)) // Simple ordering, more complex logic in production
        {
            var formattedContext = $"<context>{context}</context>";
            var contextTokenCount = EstimateTokens(formattedContext);

            if (currentTokens + ragTokens + contextTokenCount >= _options.MaxPromptTokens)
            {
                _logger.LogWarning("Skipping RAG context due to token budget. Current tokens: {CurrentTokens}, RAG tokens accumulated: {RagTokens}",
                    currentTokens, ragTokens);
                break;
            }

            ragParts.Add(formattedContext);
            ragTokens += contextTokenCount;
        }

        if (ragParts.Any())
        {
            promptParts.Add("<retrieval_augmented_context>");
            promptParts.AddRange(ragParts);
            promptParts.Add("</retrieval_augmented_context>");
            currentTokens += EstimateTokens("<retrieval_augmented_context></retrieval_augmented_context>") + ragTokens;
        }

        string finalPrompt = string.Join(Environment.NewLine, promptParts);
        _logger.LogInformation("Final prompt assembled. Estimated tokens: {EstimatedTokens} (max allowed: {MaxTokens}).",
            currentTokens, _options.MaxPromptTokens);

        return (finalPrompt, currentTokens);
    }
}

// Example usage in a Minimal API or a background service
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddLogging(configure => configure.AddConsole());
        builder.Services.Configure<TokenManagementOptions>(
            builder.Configuration.GetSection("LLM:TokenManagement"));
        builder.Services.AddSingleton<IPromptContextBuilder, PromptContextBuilder>();

        var app = builder.Build();

        app.MapGet("/chat", async (
            string query,
            IPromptContextBuilder promptBuilder,
            ILogger<Program> logger) =>
        {
            var systemMessage = "You are a helpful assistant. Be concise and answer questions about .NET development.";
            var chatHistory = new List<ChatMessage>
            {
                new("user", "What is ASP.NET Core?"),
                new("assistant", "ASP.NET Core is an open-source, cross-platform framework for building modern, cloud-based, internet-connected applications."),
                new("user", "Can it run on Linux?"),
                new("assistant", "Yes, it can run on Linux, Windows, and macOS.")
            };
            var ragContexts = new List<string>
            {
                "ASP.NET Core applications can be deployed to Docker containers.",
                "Minimal APIs in ASP.NET Core provide a streamlined way to build HTTP APIs with minimal dependencies."
            };

            // Add the current user query to the history for context building
            var fullChatHistory = chatHistory.Append(new ChatMessage("user", query)).ToList();

            var (prompt, estimatedTokens) = await promptBuilder.BuildPromptAsync(
                systemMessage,
                fullChatHistory,
                ragContexts);

            logger.LogInformation("Generated prompt for query '{Query}':\n{Prompt}\nEstimated tokens: {Tokens}",
                query, prompt, estimatedTokens);

            // In a real application, you'd send this 'prompt' to your LLM API.
            // For this example, we'll just return it.
            return Results.Ok(new { Prompt = prompt, EstimatedTokens = estimatedTokens });
        });

        await app.RunAsync();
    }
}

/*
Example appsettings.json snippet:

{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "LLM": {
    "TokenManagement": {
      "MaxPromptTokens": 1000,
      "MaxOutputTokens": 200,
      "RecentHistoryTurnsToKeep": 3,
      "TokenEstimateFactor": 3.5 // Adjust based on your model's tokenizer
    }
  },
  "AllowedHosts": "*"
}
*/
```

**Explanation of the Code Example:**

*   **`TokenManagementOptions`:** This class, configured via `IOptions` and `appsettings.json`, centralizes configurable parameters like `MaxPromptTokens`, `MaxOutputTokens`, and the number of recent chat turns to retain. This promotes maintainability and allows fine-tuning without code changes.
*   **`IPromptContextBuilder` and `PromptContextBuilder`:** We use dependency injection (`AddSingleton`) for our builder service. This makes the prompt building logic testable and swappable.
*   **Token Estimation:** The `EstimateTokens` method provides a basic character-to-token ratio. For production, you'd replace this with a robust solution like `tiktoken` (or a C# wrapper for it) to get accurate counts for specific LLMs (e.g., OpenAI models).
*   **Prioritized Prompt Construction:** The `BuildPromptAsync` method follows a clear priority order:
    1.  **System Message:** Always included first, as it defines the core behavior.
    2.  **Recent Chat History:** The `Take(_options.RecentHistoryTurnsToKeep)` ensures only the most recent turns are considered. This is a simple windowing strategy. If the total token count approaches the limit, older turns are gracefully dropped.
    3.  **RAG Contexts:** These are added last, and if needed, they are dynamically pruned based on the remaining token budget. The example uses a simple `OrderByDescending(c => c.Length)` to prioritize longer contexts, but in reality, you might use a more sophisticated relevance score from your RAG system.
*   **Logging:** Crucial for observability. We log token estimates and decisions made (e.g., skipping history or RAG contexts due to budget constraints). This helps diagnose why an LLM might be "forgetting" context or producing suboptimal results.
*   **Minimal API Integration:** The `MapGet("/chat")` endpoint demonstrates how such a builder would be consumed in a modern ASP.NET Core application, accepting a query and returning the constructed prompt.
*   **Structured Output:** The prompt uses XML-like delimiters (`<system>`, `<user>`, `<assistant>`, `<context>`) to clearly delineate different parts of the input. This is a common practice to help LLMs correctly parse structured information.

### Pitfalls and Best Practices

**Common Pitfalls:**

*   **Ignoring Token Counts:** The biggest mistake. "It works on my machine" quickly becomes "it's too expensive in production" when you haven't measured token usage.
*   **Static, Monolithic Prompts:** Using a single, massive prompt for all scenarios, regardless of user intent.
*   **Naive RAG Context Injection:** Dumping raw, unsorted, unsummarized documents directly into the prompt without filtering.
*   **Unlimited Conversational History:** Letting chat history grow indefinitely, leading to exorbitant costs and poor performance.
*   **Not Accounting for Output Tokens:** Forgetting that the model's response also consumes tokens and contributes to the budget. If `max_tokens` is too low, responses will be truncated.
*   **Lack of Observability:** No logging or monitoring of token usage, making it impossible to diagnose issues or optimize.

**Best Practices:**

*   **Measure Everything:** Integrate token counting into your application. Log input tokens, output tokens, and total tokens for every LLM call. This data is invaluable for cost analysis and performance tuning.
*   **Start Small, Scale Up:** Begin with the most concise prompt and minimal context. Only add complexity (more history, more RAG) if it genuinely improves response quality and you can justify the token cost.
*   **Tiered LLM Usage:** Use smaller, faster, cheaper models for simple tasks (summarization, classification, re-ranking) and reserve larger, more capable models for complex generation or reasoning.
*   **Implement Caching:** Cache LLM responses for common or idempotent queries to avoid redundant API calls and token consumption.
*   **Stream Responses:** When possible, consume LLM outputs via streaming (`IAsyncEnumerable`) in .NET. This improves perceived latency and allows your application to start processing chunks of the response as they arrive.
*   **Asynchronous Processing:** All interactions with LLMs should be fully asynchronous (`async`/`await`) to avoid blocking threads and maximize application throughput.
*   **A/B Test Prompt Variations:** Small changes in prompt wording or structure can significantly impact token efficiency and response quality. A/B test different approaches.
*   **Clear Delimiters:** Always use clear delimiters (e.g., `###`, `---`, `<tag>`) to separate different sections of your prompt (system instructions, user query, RAG context). This improves model understanding and can indirectly reduce token usage by making the model's task clearer.

### Conclusion

Managing LLM token growth is a critical engineering discipline, not a one-time configuration. It requires a thoughtful blend of prompt engineering, intelligent context management, and robust observability. By adopting a token-aware mindset from the outset and implementing strategies like dynamic prompt construction and smart context pruning, .NET developers can build LLM-powered applications that are not only performant and cost-effective but also scalable and maintainable in the long run. The tools and patterns in modern .NET – from dependency injection to structured logging and async programming – provide an excellent foundation for tackling this challenge head-on.
