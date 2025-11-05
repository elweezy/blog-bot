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
        if (candidates.Count == 0) return new List<Topic>();

        var candidatesJson = JsonSerializer.Serialize(candidates, JsonOptions);

        var prompt = $$"""
                       You are a senior .NET engineer and active community observer.

                       From the following JSON array of recent .NET-related posts, derive exactly 10 distinct, high-value blog topics that would interest professional .NET developers.

                       Posts JSON:
                       {{candidatesJson}}

                       ### Task
                       - Analyze the posts and group them into meaningful .NET-focused topics.
                       - Each topic must be something a .NET developer could realistically write a full blog post about.
                       - Prefer modern .NET themes: .NET 8/9, C# 12/13, ASP.NET Core, cloud-native patterns, performance, tooling, source generators, etc.
                       - Avoid vague or generic titles like “.NET Tips and Tricks”.

                       ### Output format (strict)
                       Respond with exactly 10 lines, each in this format:

                       <title> | <1–2 sentence description>

                       Rules:
                       - Use one pipe character (`|`) to separate title and description.
                       - Do not include IDs, JSON, markdown, bullet points, or extra commentary.
                       - Do not add any text before or after the 10 lines.
                       Only those 10 lines, nothing else.
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
        var prompt = $$"""
                       You are a senior .NET engineer and experienced tech blogger writing for a personal developer audience.

                       Write a long-form technical blog post for my .NET blog based on the following topic:

                       Topic JSON:
                       {{topicJson}}

                       ### Tone & Style
                       - Sound like a real developer sharing insights with peers — confident, conversational, and slightly opinionated.
                       - Use natural, human phrasing. It should feel like something I might write myself, not like a generic tutorial.
                       - Include light personal touches (“I ran into this while building…”, “You might not expect it, but…”), but keep them concise.
                       - Avoid boilerplate intros like “In this article we will…” — instead, start with a relatable problem, scenario, or question.

                       ### Technical Focus
                       - Assume readers know C# and modern .NET (8/9).
                       - Include at least one meaningful C# code sample in a fenced block (```csharp ... ```), using realistic APIs (e.g., minimal APIs, ASP.NET Core endpoints, configuration, logging, background services, source generators, performance tuning, etc.).
                       - Explain *why this topic matters now* — tie it to current .NET ecosystem trends (performance, cloud-native, DX, new language features).
                       - Call out common pitfalls, gotchas, or misunderstandings, and show better alternatives.

                       ### Structure
                       - **Intro:** relatable context or motivation for the topic.
                       - **Why it matters now:** connect to current .NET practices or releases.
                       - **Deep dive:** clear explanation of key concepts and mechanics.
                       - **Code samples:** realistic C# examples with explanation.
                       - **Pitfalls & best practices:** nuanced guidance and trade-offs.
                       - **Conclusion:** a short, human reflection or takeaway, maybe a suggestion for what to try next.

                       ### Output
                       Return only the finished article in valid GitHub-flavored Markdown.
                       Do not include JSON, metadata, or any explanation outside the post itself.
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
