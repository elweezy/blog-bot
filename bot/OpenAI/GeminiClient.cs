using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlogBot.Models;

namespace BlogBot.OpenAI;

/// <summary>
/// Google Gemini client (Gemini 1.5 Pro) – implements IOpenAIClient.
/// </summary>
public sealed class GeminiClient : IOpenAIClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;

        _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? throw new InvalidOperationException("GEMINI_API_KEY environment variable not set.");

        // Generative Language API base
        _httpClient.BaseAddress ??= new Uri("https://generativelanguage.googleapis.com/");

        // ✅ Use a current, supported model
        _model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-2.5-flash";
    }

    public async Task<List<Topic>> ClusterTopicsAsync(List<TopicCandidate> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];

        var candidatesJson = JsonSerializer.Serialize(candidates, JsonOptions);

        var prompt = $"""
                      You are a senior .NET engineer and active community observer.

                      From the following JSON array of recent .NET-related posts, identify and generate exactly 10 distinct, high-value blog topics that would interest professional .NET developers.

                      Posts JSON:
                      {candidatesJson}

                      ### Task
                      - Analyze the posts and group them into meaningful, specific, and modern .NET topics.
                      - Each topic must be something a professional .NET developer could realistically write a full technical blog post about.
                      - Focus on the latest **stable** .NET release (avoid prerelease or preview features) and relevant areas such as:
                        - C# language advancements
                        - ASP.NET Core and minimal APIs
                        - Cloud-native design and distributed systems
                        - Performance tuning and memory optimization
                        - Source generators, analyzers, and compiler features
                        - Tooling, diagnostics, observability, and developer experience (DX)
                      - Avoid vague or generic ideas like "Tips and Tricks" or "What's New in .NET" — prefer specific, actionable, and timely subjects.

                      ### Output Format (strict)
                      Respond with exactly 10 lines, each in this format:

                      <title> | <1–2 sentence description>

                      ### Rules
                      - Use one pipe character (`|`) to separate the title and description.
                      - Do not include IDs, JSON, markdown, numbering, bullet points, or commentary.
                      - Do not add any text before or after the 10 lines.
                      - The response must contain only those 10 lines, nothing else.
                      """;


        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            }
        };

        using var resp = await _httpClient.PostAsync(
            $"v1beta/models/{_model}:generateContent?key={_apiKey}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini clustering failed: {resp.StatusCode}\n{raw}");

        using var doc = JsonDocument.Parse(raw);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        if (string.IsNullOrWhiteSpace(text))
            return new List<Topic>();

        var topics = ParseTopicsFromPipeLines(text, candidates);
        return topics;
    }

    public async Task<string> GenerateBlogPostAsync(Topic topic, CancellationToken ct = default)
    {
        var topicJson = JsonSerializer.Serialize(topic, JsonOptions);
        var prompt = $"""
                      You are a seasoned .NET architect and long-time technical blogger writing for an audience of professional developers and software architects.
                      Write a high-quality, long-form technical blog post for my .NET-focused personal blog based on the following topic:
                      Topic JSON:
                      {topicJson}
                      ### Tone & Voice
                      - Write like a real engineer who has shipped production systems — confident, conversational, and slightly opinionated.
                      - Avoid "tutorial voice." Instead, share practical insights, real lessons, and the reasoning behind architectural or design decisions.
                      - Use natural phrasing and light personal reflection ("I only realized this after chasing a perf issue for hours...").
                      - Never use filler intros like "In this article..." or "Let's get started." Jump straight into a real-world scenario, problem, or challenge.
                      - Do **not** reference comments, likes, or user interaction — this blog has no comment section.

                      ### Technical Depth
                      - Assume readers are fluent in modern C# and the **latest stable release** of .NET (avoid prerelease or preview features).
                      - Include at least one complete, realistic C# code example inside a fenced code block, using triple backticks like this:
                          ```csharp
                          // realistic example code
                          ```
                        The example should demonstrate production-level patterns such as async streams, dependency injection, logging, minimal APIs, background services, configuration binding, analyzers, or source generators.
                      - Explain what the code does and why it’s written that way — highlight trade-offs, maintainability, or performance considerations.
                      - Explain why this topic is relevant now (e.g., runtime improvements, evolving cloud-native practices, modern language features, performance tuning).
                      - Identify common pitfalls or outdated patterns and show modern alternatives with reasoning.

                      ### Structure
                      - **Hook:** open with a relatable real-world story, debugging case, or architectural decision point.
                      - **Why it matters:** tie the topic to current .NET ecosystem practices or stable features.
                      - **Deep dive:** detailed explanation of key concepts, APIs, or design rationale.
                      - **Code examples:** realistic and production-relevant C# snippets, explained clearly.
                      - **Pitfalls & best practices:** experience-driven guidance and trade-offs.
                      - **Conclusion:** finish with a short reflection, takeaway, or next-step suggestion — not a summary.

                      ### Output Requirements
                      - Return only the finished article in valid GitHub-flavored Markdown.
                      - Exclude JSON, metadata, or explanations outside the article itself.
                      - The post should read like it was written by a senior .NET developer — no generic tone, no repetitive structure, no marketing language.
                      """;

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            }
        };

        using var resp = await _httpClient.PostAsync(
            $"v1beta/models/{_model}:generateContent?key={_apiKey}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini article generation failed: {resp.StatusCode}\n{raw}");

        using var doc = JsonDocument.Parse(raw);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        return text ?? string.Empty;
    }

    private sealed class TopicList
    {
        [JsonPropertyName("topics")] public List<Topic> Topics { get; set; } = new();
    }

    private List<Topic> ParseTopicsFromPipeLines(string raw, List<TopicCandidate> candidates)
    {
        var result = new List<Topic>();

        var lines = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        foreach (var line in lines)
        {
            // Expect format: <title> | <description>
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2) continue;

            var title = parts[0];
            var description = string.Join(" | ", parts.Skip(1));

            // ✅ Generate a consistent slug ID
            var id = Slugify(title);

            // Try to find a relevant candidate link (optional)
            var primaryUrl = candidates
                .OrderByDescending(c => SimilarityScore(c.Title, title))
                .FirstOrDefault()?.Url ?? string.Empty;

            result.Add(new Topic
            {
                Id = id,
                Title = title,
                Description = description,
                PrimaryUrl = primaryUrl,
                SupportingUrls = string.IsNullOrEmpty(primaryUrl)
                    ? new List<string>()
                    : new List<string> { primaryUrl },
                AggregateScore = 0.0
            });
        }

        return result.Take(10).ToList();
    }

    private string Slugify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = text.ToLowerInvariant();

        // Replace "c#" → "csharp" and ".net" → "net"
        text = text.Replace("c#", "csharp").Replace(".net", "net");

        // Remove invalid characters
        text = Regex.Replace(text, @"[^a-z0-9\s-]", "");

        // Replace whitespace or underscores with hyphens
        text = Regex.Replace(text, @"[\s_]+", "-");

        // Collapse multiple hyphens
        text = Regex.Replace(text, "-{2,}", "-").Trim('-');

        return text;
    }

    private double SimilarityScore(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return 0;

        source = source.ToLowerInvariant();
        target = target.ToLowerInvariant();

        // Simple heuristic: count of common words
        var srcWords = source.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tgtWords = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var overlap = tgtWords.Count(w => srcWords.Contains(w));
        return overlap;
    }
}