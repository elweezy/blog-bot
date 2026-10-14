using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlogBot.Models;
using BlogBot.Utils;

namespace BlogBot.Llm;

/// <summary>
/// Google Gemini API client (free tier). Tries each model in GEMINI_MODELS in order, so a quota hit or a retired
/// model falls through to the next one instead of failing the run.
/// </summary>
public sealed class GeminiClient : ILlmClient
{
    private const string DefaultModels = "gemini-3.8-flash,gemini-3.6-flash,gemini-2.5-flash";
    private const int MaxAttemptsPerModel = 3;

    private readonly HttpClient _httpClient;
    private readonly string[] _models;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public GeminiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;

        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("GEMINI_API_KEY environment variable not set.");

        _httpClient.BaseAddress ??= new Uri("https://generativelanguage.googleapis.com/");
        // Header instead of ?key= so the key never ends up in logged URLs.
        _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        // The workflow passes an empty string when the GEMINI_MODELS repo variable isn't set.
        var models = Environment.GetEnvironmentVariable("GEMINI_MODELS");
        _models = (string.IsNullOrWhiteSpace(models) ? DefaultModels : models)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<List<Topic>> ProposeTopicsAsync(
        IReadOnlyList<TopicCandidate> candidates,
        IReadOnlyList<string> excludedTitles,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];

        var compact = candidates.Select((c, i) => new
        {
            i,
            source = c.Source,
            title = c.Title,
            summary = c.Summary.Length > 200 ? c.Summary[..200] : c.Summary
        });

        var schema = new
        {
            type = "ARRAY",
            items = new
            {
                type = "OBJECT",
                properties = new Dictionary<string, object>
                {
                    ["title"] = new { type = "STRING" },
                    ["angle"] = new { type = "STRING" },
                    ["summary"] = new { type = "STRING" },
                    ["sourceIndexes"] = new { type = "ARRAY", items = new { type = "INTEGER" } }
                },
                required = new[] { "title", "angle", "summary", "sourceIndexes" }
            }
        };

        var proposals = await CallAsync(
            "topics",
            Prompts.TopicsSystem,
            Prompts.TopicsUser(JsonSerializer.Serialize(compact), excludedTitles),
            schema,
            json => JsonSerializer.Deserialize<List<TopicProposal>>(json, JsonOptions)
                    ?? throw new JsonException("Topic list was null."),
            ct);

        return proposals
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Select(p =>
            {
                var sources = p.SourceIndexes
                    .Where(i => i >= 0 && i < candidates.Count)
                    .Distinct()
                    .Select(i => candidates[i])
                    .ToList();

                return new Topic
                {
                    Id = Slug.From(p.Title),
                    Title = p.Title.Trim(),
                    Angle = p.Angle.Trim(),
                    Description = p.Summary.Trim(),
                    Sources = sources,
                    PrimaryUrl = sources.FirstOrDefault()?.Url ?? string.Empty,
                    SupportingUrls = sources.Select(s => s.Url).ToList()
                };
            })
            .ToList();
    }

    public Task<string> WriteDraftAsync(Topic topic, string sourceMaterial, string? authorProfile, CancellationToken ct = default) =>
        CallAsync("draft", Prompts.WriterSystem(authorProfile), Prompts.WriterUser(topic, sourceMaterial), schema: null, text => text, ct);

    public async Task<GeneratedPost> EditPostAsync(
        Topic topic,
        string draft,
        string sourceMaterial,
        string? authorProfile,
        IReadOnlyList<string> issues,
        CancellationToken ct = default)
    {
        var schema = new
        {
            type = "OBJECT",
            properties = new Dictionary<string, object>
            {
                ["markdown"] = new { type = "STRING" },
                ["description"] = new { type = "STRING" },
                ["tags"] = new { type = "ARRAY", items = new { type = "STRING", @enum = TagVocabulary.All } }
            },
            required = new[] { "markdown", "description", "tags" }
        };

        return await CallAsync(
            "edit",
            Prompts.EditorSystem(authorProfile),
            Prompts.EditorUser(topic, draft, sourceMaterial, issues),
            schema,
            json =>
            {
                var post = JsonSerializer.Deserialize<GeneratedPost>(json, JsonOptions);
                if (post is null || string.IsNullOrWhiteSpace(post.Markdown))
                    throw new JsonException("Editor returned no markdown.");
                return post;
            },
            ct);
    }

    public Task<List<ClaimVerdict>> VerifyClaimsAsync(IReadOnlyList<ClaimCheck> claims, string model, CancellationToken ct = default)
    {
        var schema = new
        {
            type = "ARRAY",
            items = new
            {
                type = "OBJECT",
                properties = new Dictionary<string, object>
                {
                    ["id"] = new { type = "INTEGER" },
                    ["supported"] = new { type = "BOOLEAN" },
                    ["reason"] = new { type = "STRING" }
                },
                required = new[] { "id", "supported", "reason" }
            }
        };

        // Models handle a plain 0..n-1 numbering far better than our internal ids (-1, 10000, ...), so the
        // verifier sees positions and the answers are mapped back.
        var payload = JsonSerializer.Serialize(claims.Select((c, i) => new { id = i, kind = c.Kind, text = c.Text, evidence = c.Evidence }));

        // Only this one model: each verifier slot gives an independent verdict. Partial answers are returned as-is;
        // the caller asks again for anything missing.
        return CallAsync("fact-check", Prompts.VerifierSystem, Prompts.VerifierUser(payload), schema, json =>
        {
            var verdicts = JsonSerializer.Deserialize<List<ClaimVerdict>>(json, JsonOptions) ?? [];
            if (verdicts.Count == 0)
                throw new JsonException("Verifier returned no verdicts.");

            return verdicts
                .Where(v => v.Id >= 0 && v.Id < claims.Count)
                .DistinctBy(v => v.Id)
                .Select(v => new ClaimVerdict { Id = claims[v.Id].Id, Supported = v.Supported, Reason = v.Reason })
                .ToList();
        }, ct, [model]);
    }

    public Task<Dictionary<int, string>> RepairSentencesAsync(IReadOnlyList<RepairRequest> requests, CancellationToken ct = default)
    {
        var schema = new
        {
            type = "ARRAY",
            items = new
            {
                type = "OBJECT",
                properties = new Dictionary<string, object>
                {
                    ["id"] = new { type = "INTEGER" },
                    ["replacement"] = new { type = "STRING" }
                },
                required = new[] { "id", "replacement" }
            }
        };

        // Same 0..n-1 numbering as the fact-check, mapped back afterwards.
        var payload = JsonSerializer.Serialize(requests.Select((r, i) =>
            new { id = i, kind = r.Kind, sentence = r.Text, paragraph = r.Paragraph, rejected_because = r.Reason, evidence = r.Evidence }));

        return CallAsync("repair", Prompts.RepairSystem, Prompts.RepairUser(payload), schema, json =>
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<int, string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var i = item.GetProperty("id").GetInt32();
                if (i >= 0 && i < requests.Count)
                    result[requests[i].Id] = item.GetProperty("replacement").GetString() ?? string.Empty;
            }

            return result;
        }, ct);
    }

    public async Task<string> RewriteTeaserAsync(IReadOnlyList<string> verifiedSentences, CancellationToken ct = default)
    {
        var teaser = await CallAsync("teaser", Prompts.TeaserSystem, Prompts.TeaserUser(verifiedSentences), schema: null, text => text, ct);
        return teaser.Trim().Trim('"').Trim();
    }

    public async Task<string> FixDiagramAsync(string diagram, string error, CancellationToken ct = default)
    {
        var fixedDiagram = await CallAsync("diagram fix", Prompts.FixDiagramSystem, Prompts.FixDiagramUser(diagram, error), schema: null, text => text, ct);
        return fixedDiagram.Trim().Replace("```mermaid", "").Replace("```", "").Trim();
    }

    /// <summary>
    /// Sends one prompt, trying each model in turn. Anything that makes the answer unusable (HTTP errors, timeouts,
    /// truncated output, JSON that doesn't parse) counts as a failed attempt, so it is retried and then falls through
    /// to the next model.
    /// </summary>
    private async Task<T> CallAsync<T>(string step, string system, string user, object? schema, Func<string, T> parse, CancellationToken ct,
        IReadOnlyList<string>? models = null)
    {
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = schema is null
                ? null
                : new { responseMimeType = "application/json", responseSchema = schema }
        };
        var payload = JsonSerializer.Serialize(body, JsonOptions);

        var failures = new List<string>();
        foreach (var model in models ?? _models)
        {
            for (var attempt = 1; attempt <= MaxAttemptsPerModel; attempt++)
            {
                var (result, failure, tryNextModel) = await TryCallAsync(model, payload, parse, ct);
                if (failure is null)
                {
                    Console.WriteLine($"Gemini [{step}] succeeded with {model}.");
                    return result!;
                }

                failures.Add($"{model} (attempt {attempt}): {failure}");
                Console.Error.WriteLine($"Gemini [{step}] {model} attempt {attempt} failed: {failure}");

                if (tryNextModel) break;
                if (attempt < MaxAttemptsPerModel)
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt * attempt), ct);
            }
        }

        throw new InvalidOperationException("All Gemini models failed:\n" + string.Join("\n", failures));
    }

    private async Task<(T? Result, string? Failure, bool TryNextModel)> TryCallAsync<T>(
        string model, string payload, Func<string, T> parse, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _httpClient.PostAsync($"v1beta/models/{model}:generateContent", content, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // Unknown model or a request this model rejects: retrying won't help.
                var permanent = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden;
                return (default, $"{(int)resp.StatusCode} {Truncate(raw, 300)}", permanent);
            }

            var (text, finishReason) = ExtractText(raw);
            if (finishReason is not null and not "STOP")
                return (default, $"finishReason {finishReason} (output incomplete)", false);
            if (string.IsNullOrWhiteSpace(text))
                return (default, $"empty response ({Truncate(raw, 300)})", false);

            return (parse(text), null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (default, $"{ex.GetType().Name}: {ex.Message}", false);
        }
    }

    private static (string? Text, string? FinishReason) ExtractText(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return (null, null);

        var candidate = candidates[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;

        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts))
            return (null, finishReason);

        // Thinking models can return thought parts; keep only the answer text.
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            var isThought = part.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.True;
            if (!isThought && part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }

        return (sb.ToString(), finishReason);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class TopicProposal
    {
        public string Title { get; set; } = string.Empty;
        public string Angle { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<int> SourceIndexes { get; set; } = new();
    }
}
