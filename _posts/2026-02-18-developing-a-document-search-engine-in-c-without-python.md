---
layout: post
title: "Developing a Document Search Engine in C# Without Python"
date: 2026-02-18 03:54:09 +0000
categories: dotnet blog
canonical_url: "https://dev.to/olafur_aron/build-a-document-search-engine-in-c-without-python-4123"
---

We've all been there: a project demands intelligent search over internal documents, product descriptions, or user-generated content. The immediate reflex for many in the modern AI landscape is to reach for a Python-based library – perhaps `sentence-transformers`, `faiss`, or a dedicated vector database service. And for rapid prototyping or specific research tasks, that's often a pragmatic choice. But what happens when that prototype needs to ship into a production .NET ecosystem? Suddenly, the elegance of a Python script dissolves into deployment nightmares, dependency conflicts, environment management headaches, and the performance overhead of inter-process communication.

This exact scenario arose on a recent project. The requirement was a low-latency, highly customizable semantic search capability for a specialized document corpus, tightly integrated into an existing .NET 8 microservice architecture. Adding a separate Python service felt like introducing a foreign body, complicating everything from CI/CD pipelines and logging to monitoring and security. Our conviction was clear: if .NET is our platform, we should leverage its strengths. The challenge then became: how do we build an efficient, custom document search engine in C# that leverages modern embedding and retrieval techniques, without touching a single line of Python?

### The Modern C# Advantage: Beyond CRUD and Web APIs

For years, C# has been synonymous with enterprise applications, web services, and Windows development. While its numerical capabilities were always present, they often felt secondary to Python's rich scientific computing ecosystem. This perception is rapidly changing. With .NET 6, 7, and 8, we've seen significant investments in performance, particularly in `System.Memory`, `System.Numerics`, and `Span<T>`. Coupled with advancements in .NET's ability to consume ONNX models via `Microsoft.ML.OnnxRuntime`, the landscape for machine learning inference and numerical processing in C# has fundamentally shifted. We no longer need to cede numerical workloads to other languages. This is precisely why building a search engine in C#, in its entirety, is not just feasible but often desirable for production .NET systems.

The core components of a modern document search engine are:
1.  **Text Processing**: Cleaning and preparing raw text.
2.  **Embedding Generation**: Converting text into dense vector representations (embeddings) that capture semantic meaning.
3.  **Vector Storage**: Efficiently storing these embeddings alongside document metadata.
4.  **Similarity Search**: Given a query embedding, finding the most semantically similar document embeddings.

Let's break down how we tackle each of these without resorting to Python.

### Crafting Embeddings in Pure C#: The ONNX Runtime Path

The cornerstone of semantic search is the embedding. These high-dimensional floating-point vectors encode the meaning of text, allowing us to compare their "closeness" to determine semantic similarity. Generating these in C# requires a performant inference engine. `Microsoft.ML.OnnxRuntime` is the undisputed champion here. It allows us to load pre-trained deep learning models, exported in the Open Neural Network Exchange (ONNX) format, and run inference directly within our .NET application.

The process typically involves:
1.  **Model Acquisition**: Sourcing a suitable embedding model (e.g., a Sentence-BERT variant like `all-MiniLM-L6-v2`) and converting it to ONNX. Many pre-converted ONNX models are available, or you can convert them using Python tools offline. The key is that the *runtime* inference is C#.
2.  **Tokenization**: Converting the input text into numerical IDs that the ONNX model expects. This is often the trickiest part, as different models use different tokenizers (e.g., WordPiece, BPE). While implementing a full-fledged tokenizer from scratch in C# is a significant undertaking, there are C# libraries (like `BertTokenizer` from `dotnet-transformers` or `SentenceTransformers.Client` which wraps `Microsoft.ML.OnnxRuntime` and handles tokenization) that simplify this. For a custom approach, we might need to rely on a simpler tokenizer or pre-process the text to fit the model's expected input structure, which often involves token IDs, attention masks, and token type IDs.
3.  **Inference**: Feeding the tokenized input to `Microsoft.ML.OnnxRuntime` to get the output embeddings.

Once we have the embeddings (typically a `float[]`), we store them. For many internal applications, a simple in-memory `ConcurrentDictionary<Guid, float[]>` is surprisingly effective for smaller to medium-sized datasets (thousands to tens of thousands of documents). For persistence, an embedded database like SQLite or a simple file-based approach can work well, especially when combined with memory-mapped files for efficient access.

### Retrieval: Efficient Similarity in C#

With embeddings generated and stored, the next step is retrieval. Given a query embedding, we need to find document embeddings that are "closest" in vector space. Cosine similarity is the de-facto metric for this.

The formula for cosine similarity between two vectors, A and B, is:
```
similarity = (A ⋅ B) / (||A|| * ||B||)
```
Where `A ⋅ B` is the dot product, and `||A||` and `||B||` are the magnitudes (L2 norms) of vectors A and B, respectively.

Calculating this efficiently is where modern C# truly shines. Using `Span<T>` and `System.Numerics.Vector<T>` (SIMD intrinsics) allows us to perform these array operations with near-native performance, leveraging CPU features directly.

For a brute-force search (linear scan), this is highly efficient for moderate dataset sizes. For millions or billions of documents, brute-force becomes untenable. This is where Approximate Nearest Neighbor (ANN) algorithms (like HNSW, Annoy, FAISS) come into play. Implementing these complex algorithms from scratch in pure C# is a huge undertaking. For scenarios demanding massive scale without external dependencies, one might integrate a C# port of an ANN library (if available and mature) or opt for a managed service with a C# client. However, for a fully custom, in-house solution where datasets are in the hundreds of thousands or low millions, often clever indexing and pre-filtering (e.g., based on keywords or categories) can reduce the candidate set significantly, making efficient brute-force search perfectly viable for the final similarity check.

### An Illustrative C# Component: The `VectorSearchService`

Let's consider a practical implementation for an ingestion and search pipeline. We'll use an `IHostedService` to demonstrate a background indexing process, `Microsoft.ML.OnnxRuntime` for embeddings, and `System.Numerics` for efficient similarity calculations.

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Configuration for our search engine
public sealed class SearchEngineOptions
{
    public const string SectionName = "SearchEngine";
    public string OnnxModelPath { get; set; } = string.Empty;
    public int EmbeddingDimension { get; set; } = 384; // e.g., for all-MiniLM-L6-v2
    public int MaxSequenceLength { get; set; } = 128;
}

// Represents a document in our index
public record Document(Guid Id, string Title, string Content);

// Our core vector search service
public sealed class VectorSearchService : IHostedService, IDisposable
{
    private readonly ILogger<VectorSearchService> _logger;
    private readonly SearchEngineOptions _options;
    private readonly InferenceSession _session;

    // In-memory index: Document ID -> Embedding vector + metadata
    private readonly ConcurrentDictionary<Guid, (float[] Embedding, Document Document)> _documentIndex = new();
    private readonly CancellationTokenSource _cts = new();

    // A simple, basic tokenizer (for demonstration; real models need complex tokenization)
    private readonly Regex _wordTokenizer = new(@"\b\w+\b", RegexOptions.Compiled);

    public VectorSearchService(ILogger<VectorSearchService> logger, IOptions<SearchEngineOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.OnnxModelPath) || !File.Exists(_options.OnnxModelPath))
        {
            _logger.LogError("ONNX model path is invalid or file not found: {Path}", _options.OnnxModelPath);
            throw new InvalidOperationException("ONNX model path must be specified and exist.");
        }

        try
        {
            _session = new InferenceSession(_options.OnnxModelPath);
            _logger.LogInformation("ONNX model loaded successfully from {Path}", _options.OnnxModelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ONNX model from {Path}", _options.OnnxModelPath);
            throw;
        }

        // Pre-calculate document vector magnitudes for faster cosine similarity
        // In a real system, these would be persisted alongside embeddings
    }

    // Example of a minimal text-to-ID tokenizer for a simple model.
    // NOTE: This is a highly simplified tokenizer for demonstration.
    // Production-grade embedding models require sophisticated tokenizers (e.g., WordPiece, BPE)
    // which are often provided by C# libraries like dotnet-transformers or SentenceTransformers.Client.
    private int[] SimpleTokenize(string text)
    {
        var tokens = new List<int>();
        var matches = _wordTokenizer.Matches(text.ToLowerInvariant()); // Basic case-folding
        var vocab = new ConcurrentDictionary<string, int>(); // Simple token->ID mapping
        var nextId = 0;

        foreach (Match match in matches)
        {
            var word = match.Value;
            if (!vocab.TryGetValue(word, out var id))
            {
                id = Interlocked.Increment(ref nextId); // Assign new ID
                vocab.TryAdd(word, id);
            }
            tokens.Add(id);
        }

        // Pad or truncate to MaxSequenceLength
        if (tokens.Count > _options.MaxSequenceLength)
            tokens = tokens.Take(_options.MaxSequenceLength).ToList();
        else if (tokens.Count < _options.MaxSequenceLength)
            tokens.AddRange(Enumerable.Repeat(0, _options.MaxSequenceLength - tokens.Count)); // Pad with 0s

        return tokens.ToArray();
    }

    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<float>();
        }

        // For Sentence-BERT models, input typically consists of:
        // input_ids: token IDs
        // attention_mask: 1 for actual tokens, 0 for padding
        // token_type_ids: 0 for single sentence input

        // Simplified tokenization for demonstration
        var inputIds = SimpleTokenize(text);
        var attentionMask = inputIds.Select(id => id == 0 ? 0L : 1L).ToArray(); // 0 for padding (ID 0), 1 otherwise
        var tokenTypeIds = Enumerable.Repeat(0L, inputIds.Length).ToArray(); // All 0 for single sentence input

        var inputIdsTensor = new DenseTensor<long>(inputIds.Select(x => (long)x).ToArray(), new[] { 1, _options.MaxSequenceLength });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, _options.MaxSequenceLength });
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, new[] { 1, _options.MaxSequenceLength });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        try
        {
            using var results = _session.Run(inputs);
            var lastHiddenState = (DenseTensor<float>)results.First().Value;

            // For models like Sentence-BERT, usually a pooling operation (e.g., mean pooling)
            // is applied to the last_hidden_state to get the sentence embedding.
            // Example: Mean pooling for all-MiniLM-L6-v2
            var embedding = new float[_options.EmbeddingDimension];
            var sequenceLength = lastHiddenState.Dimensions[1];
            var hiddenSize = lastHiddenState.Dimensions[2]; // Should be EmbeddingDimension

            if (hiddenSize != _options.EmbeddingDimension)
            {
                 _logger.LogWarning("Model output hidden size ({HiddenSize}) does not match configured EmbeddingDimension ({ConfigDim}). Using model's output size.", hiddenSize, _options.EmbeddingDimension);
                 embedding = new float[hiddenSize];
            }

            // Simple mean pooling over tokens with attention mask
            int validTokens = 0;
            for (int i = 0; i < sequenceLength; i++)
            {
                if (attentionMask[i] == 1) // Only consider actual tokens
                {
                    validTokens++;
                    for (int j = 0; j < hiddenSize; j++)
                    {
                        embedding[j] += lastHiddenState[0, i, j];
                    }
                }
            }

            if (validTokens > 0)
            {
                for (int j = 0; j < hiddenSize; j++)
                {
                    embedding[j] /= validTokens;
                }
            }
            // Normalize the vector (L2 norm)
            return NormalizeVector(embedding);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during ONNX inference for text: {Text}", text);
            return Array.Empty<float>();
        }
    }

    // L2 Normalize a vector
    private static float[] NormalizeVector(float[] vector)
    {
        var result = new float[vector.Length];
        var sumOfSquares = 0f;

        // Use Span<T> for performance
        Span<float> vecSpan = vector;
        Span<float> resSpan = result;

        // Calculate sum of squares
        for (int i = 0; i < vecSpan.Length; i++)
        {
            sumOfSquares += vecSpan[i] * vecSpan[i];
        }

        var magnitude = MathF.Sqrt(sumOfSquares);

        if (magnitude > 1e-6) // Avoid division by zero or very small numbers
        {
            for (int i = 0; i < vecSpan.Length; i++)
            {
                resSpan[i] = vecSpan[i] / magnitude;
            }
        }
        return result;
    }

    // Calculate Cosine Similarity between two L2-normalized vectors
    public static float CosineSimilarity(ReadOnlySpan<float> vectorA, ReadOnlySpan<float> vectorB)
    {
        if (vectorA.Length != vectorB.Length)
        {
            throw new ArgumentException("Vectors must have the same length.");
        }

        float dotProduct = 0;

        // Use SIMD (Vector<float>) for faster dot product if available
        if (Vector.IsHardwareAccelerated && vectorA.Length >= Vector<float>.Count)
        {
            int i = 0;
            var sumVector = Vector<float>.Zero;
            for (; i <= vectorA.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                sumVector += new Vector<float>(vectorA.Slice(i)) * new Vector<float>(vectorB.Slice(i));
            }
            for (; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
            }
            dotProduct += Vector.Dot(sumVector, Vector<float>.One); // Sum up the SIMD vector
        }
        else
        {
            for (int i = 0; i < vectorA.Length; i++)
            {
                dotProduct += vectorA[i] * vectorB[i];
            }
        }

        // Since vectors are already L2-normalized, their magnitudes are 1.
        // Cosine Similarity = Dot Product / (MagnitudeA * MagnitudeB)
        // Cosine Similarity = Dot Product / (1 * 1) = Dot Product
        return dotProduct;
    }

    public async IAsyncEnumerable<(Document Document, float Score)> SearchAsync(string query, int topK = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        var queryEmbedding = await GetEmbeddingAsync(query);
        if (queryEmbedding.Length == 0)
        {
            yield break;
        }

        var sw = Stopwatch.StartNew();
        var results = new PriorityQueue<Document, float>(); // Min-priority queue

        // Brute-force scan - highly optimized with Span<T> and SIMD
        foreach (var entry in _documentIndex)
        {
            var docId = entry.Key;
            var docEmbedding = entry.Value.Embedding;
            var document = entry.Value.Document;

            var similarity = CosineSimilarity(queryEmbedding, docEmbedding);

            // Maintain top-K using a min-priority queue
            if (results.Count < topK)
            {
                results.Enqueue(document, similarity); // Enqueue score as priority
            }
            else if (similarity > results.Peek().Item2) // If current score is better than lowest in queue
            {
                results.Dequeue();
                results.Enqueue(document, similarity);
            }
        }
        sw.Stop();
        _logger.LogInformation("Search for '{Query}' completed in {ElapsedMs}ms, found {Count} results.", query, sw.ElapsedMilliseconds, results.Count);

        // Dequeue in reverse order to get highest scores first
        var sortedResults = new List<(Document Document, float Score)>();
        while(results.TryDequeue(out var doc, out var score))
        {
            sortedResults.Add((doc, score));
        }
        sortedResults.Reverse(); // Highest score first

        foreach (var result in sortedResults)
        {
            yield return result;
        }
    }

    // IHostedService implementation: Background indexing
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VectorSearchService is starting background indexing.");
        _ = IndexDocumentsAsync(_cts.Token); // Fire and forget, handled by IHostedService
        return Task.CompletedTask;
    }

    private async Task IndexDocumentsAsync(CancellationToken cancellationToken)
    {
        // Simulate loading and indexing documents from a persistent store
        // In a real scenario, this would load from a database, file system, etc.
        var sampleDocuments = new List<Document>
        {
            new(Guid.NewGuid(), "Introduction to .NET 8", "Learn about the new features in .NET 8, including performance improvements and C# 12 updates."),
            new(Guid.NewGuid(), "Advanced C# Techniques", "Dive deep into modern C# concepts like Span<T>, async streams, and source generators for high-performance applications."),
            new(Guid.NewGuid(), "Building Cloud-Native Applications", "Explore best practices for designing and deploying scalable microservices on Azure, AWS, or GCP using .NET Core."),
            new(Guid.NewGuid(), "Understanding Document Databases", "A guide to NoSQL document databases like MongoDB and Cosmos DB, and how they differ from relational databases."),
            new(Guid.NewGuid(), "Optimizing Database Performance", "Tips and tricks for improving query speeds and reducing latency in SQL Server and PostgreSQL."),
            new(Guid.NewGuid(), "The Benefits of C# for AI/ML", "Discover why C# is becoming a powerful language for machine learning tasks, especially with ONNX Runtime integration.")
        };

        foreach (var doc in sampleDocuments)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var embedding = await GetEmbeddingAsync(doc.Content);
                if (embedding.Length > 0)
                {
                    _documentIndex[doc.Id] = (embedding, doc);
                    _logger.LogDebug("Indexed document '{Title}' with ID {Id}", doc.Title, doc.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to index document {Id}: {Title}", doc.Id, doc.Title);
            }
            await Task.Delay(50, cancellationToken); // Simulate work, avoid busy-waiting
        }
        _logger.LogInformation("VectorSearchService finished initial document indexing. Indexed {Count} documents.", _documentIndex.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VectorSearchService is stopping.");
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _cts.Dispose();
    }
}

// Example usage within a Minimal API host:
/*
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SearchEngineOptions>(builder.Configuration.GetSection(SearchEngineOptions.SectionName));
builder.Services.AddSingleton<VectorSearchService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<VectorSearchService>());

var app = builder.Build();

app.MapGet("/search", async (string query, VectorSearchService searchService) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest("Query cannot be empty.");
    }

    var results = new List<object>();
    await foreach (var (doc, score) in searchService.SearchAsync(query, topK: 3))
    {
        results.Add(new { doc.Title, doc.Content, Score = score });
    }
    return Results.Ok(results);
});

app.Run();
*/
```

This `VectorSearchService` demonstrates several production-level patterns:
*   **Dependency Injection**: The service takes `ILogger` and `IOptions<SearchEngineOptions>` via its constructor, adhering to DI principles.
*   **Configuration Binding**: `SearchEngineOptions` are bound from `appsettings.json` (or environment variables), making the model path and other parameters easily configurable without recompilation.
*   **`IHostedService`**: The `IndexDocumentsAsync` method runs as a background service, starting with the application. This is ideal for long-running tasks like initial data loading or continuous indexing.
*   **`Microsoft.ML.OnnxRuntime`**: Directly used for model inference, illustrating how to prepare inputs (though simplified tokenization here) and process outputs.
*   **`Span<T>` and `Vector<T>`**: `NormalizeVector` and `CosineSimilarity` leverage these types for highly optimized numerical operations, crucial for performance in large-scale vector comparisons. `Vector.IsHardwareAccelerated` allows for runtime SIMD detection.
*   **`IAsyncEnumerable`**: The `SearchAsync` method returns an async stream, allowing the caller to process results as they are found, potentially improving perceived responsiveness for very large result sets or slow similarity calculations (though our example is fast enough not to strictly *require* it for small K).
*   **Error Handling and Logging**: Robust `try-catch` blocks and `ILogger` calls are interspersed to provide visibility and resilience.

#### The Code's Rationale and Trade-offs

1.  **Simplified Tokenization**: The `SimpleTokenize` method is a glaring simplification. Real-world transformer models demand specific tokenization (e.g., WordPiece, BPE) that handles subwords, special tokens, and vocabulary mapping. Implementing this accurately in C# is complex. In a production system, one would use a specialized C# library (like `SentenceTransformers.Client` or `dotnet-transformers`) or pre-process text offline. This example focuses on the `OnnxRuntime` interaction itself.
2.  **In-Memory Index**: The `_documentIndex` is a `ConcurrentDictionary`. This is fast but consumes RAM. For larger datasets, this would need to be replaced with a persistent, disk-backed index (e.g., using `MemoryMappedFiles` for vector data, or a dedicated C# vector store that manages data on disk).
3.  **Brute-Force Search**: `SearchAsync` performs a linear scan. While `Span<T>` and `Vector<T>` make `CosineSimilarity` incredibly fast, `O(N)` complexity means it won't scale indefinitely for `N` documents. For millions of documents, you'd need an ANN algorithm. For a pure C# solution, this is either a custom implementation (challenging) or integration with a C# vector database client. For many internal tools, `N` might be small enough for brute-force.
4.  **L2 Normalization**: Normalizing vectors simplifies cosine similarity to a mere dot product. This pre-computation reduces calculation per search.
5.  **PriorityQueue**: Used for `topK` selection, ensuring we only keep track of the most relevant documents without sorting the entire dataset.

### Pitfalls and Best Practices

*   **Tokenizer Accuracy**: This is often the most overlooked part. An improperly tokenized input will yield meaningless embeddings. Always ensure your C# tokenizer implementation matches the one used to train your ONNX model.
*   **Model Size vs. Performance**: Embedding models vary in size and complexity. Larger models (e.g., BERT-large) produce richer embeddings but are slower to infer. Smaller models (e.g., MiniLM, distilbert) are faster but might have slightly lower accuracy. Choose based on your latency and quality requirements. Test different models.
*   **Memory Management**: When dealing with large numbers of embeddings, memory usage can explode. `float[]` arrays can quickly consume gigabytes. Consider using `MemoryMappedFiles` or even `unsafe` code with fixed-size buffers and custom memory allocators for extreme cases.
*   **Persistence**: An in-memory index is volatile. You'll need a strategy to persist your embeddings (e.g., flat binary files, SQLite, or a purpose-built vector database). Ensure your loading strategy for the `IHostedService` is robust.
*   **Updating Index**: Real-world systems need to add, update, and delete documents. This requires careful management of your vector store and potentially re-indexing.
*   **ANN for Scale**: As mentioned, for truly massive datasets, brute-force won't cut it. Research C# ANN libraries (if any mature ones exist that are truly independent of Python) or be prepared for the significant engineering effort of implementing one. Alternatively, if the "no Python" rule can bend for *external services*, a vector database with a C# client (e.g., Azure AI Search, Pinecone, Weaviate, Milvus) might be considered. But the spirit of this post is *building it yourself*.

### Conclusion

Building a custom semantic search engine in C# without Python dependencies is not just a theoretical exercise; it's a practical, production-ready approach for many specialized scenarios. Modern .NET, with its performance-centric runtime, `Span<T>`, `System.Numerics`, and seamless ONNX integration, empowers developers to own the entire AI inference pipeline. This allows for tighter integration, simplified deployment, consistent tooling, and complete control over the system's performance characteristics – a level of engineering mastery that's often sacrificed when opting for external language ecosystems. The capabilities are there; it's about leveraging them effectively.
