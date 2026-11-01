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
public sealed partial class GeminiClient : ILlmClient
{
    private const string DefaultModels = "gemini-3.8-flash,gemini-3.6-flash,gemini-2.5-flash";
    private const int MaxAttemptsPerModel = 3;

    // Free-tier safety. Limits are per project and shown in the AI Studio rate-limit dashboard; gemini-3.8-flash
    // allows 5 requests a minute. So: one call at a time, at least GEMINI_MIN_SECONDS_BETWEEN_CALLS apart (default
    // 13 s, about 4.6 a minute), and at most GEMINI_MAX_CALLS_PER_RUN calls per run (default 30; a post needs
    // about 10-16).
    private static readonly SemaphoreSlim CallGate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;
    private static int _callsThisRun;
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(EnvInt("GEMINI_MIN_SECONDS_BETWEEN_CALLS", 13));
    private static readonly int MaxCallsPerRun = EnvInt("GEMINI_MAX_CALLS_PER_RUN", 30);

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
            using var resp = await PacedPostAsync(model, content, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            // 402: the project has no usable billing (e.g. prepay credits at $0). That applies to every model, so stop
            // the run right away with an explanation instead of retrying each model.
            if (resp.StatusCode == HttpStatusCode.PaymentRequired)
                throw new GeminiBillingException(
                    "Gemini API billing problem (HTTP 402): " + Truncate(raw, 300) + "\n" +
                    "The API key's Google project is linked to a billing account without credits. To use the free " +
                    "tier, disable billing on that project (console.cloud.google.com/billing/projects) or create the " +
                    "key in a project with no billing, then update the GEMINI_API_KEY secret.");

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Daily free quota: nothing will succeed until it resets, so stop the run with an explanation.
                if (DailyQuota().IsMatch(raw))
                    throw new GeminiQuotaException(
                        $"Gemini free-tier daily quota reached for {model}: {Truncate(raw, 300)}\n" +
                        "Daily limits reset at midnight Pacific time; the next run will work again.");

                // Per-minute limit: wait as long as Google asks (capped), then let the retry loop try again.
                var wait = RetryDelay().Match(raw) is { Success: true } m ? TimeSpan.FromSeconds(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)) : TimeSpan.FromSeconds(30);
                if (wait > TimeSpan.FromSeconds(90)) wait = TimeSpan.FromSeconds(90);
                Console.WriteLine($"  Gemini rate limit on {model}; waiting {wait.TotalSeconds:0} s as requested.");
                await Task.Delay(wait, ct);
                return (default, $"429 rate limited (waited {wait.TotalSeconds:0} s)", false);
            }

            if (!resp.IsSuccessStatusCode)
            {
                // Unknown model or a request this model rejects: retrying won't help.
                // 503 means the model is overloaded right now: on the free tier that tends to last, so switch to the
                // next model instead of retrying the same one.
                var permanent = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                    or HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable;
                return (default, $"{(int)resp.StatusCode} {Truncate(raw, 300)}", permanent);
            }

            var (text, finishReason) = ExtractText(raw);
            if (finishReason is not null and not "STOP")
                return (default, $"finishReason {finishReason} (output incomplete)", false);
            if (string.IsNullOrWhiteSpace(text))
                return (default, $"empty response ({Truncate(raw, 300)})", false);

            return (parse(text), null, false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The call hung until the HTTP timeout: an overloaded model, so move on to the next one.
            return (default, $"timed out ({ex.Message})", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return (default, $"{ex.GetType().Name}: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Sends one request while enforcing the free-tier safety limits: one call at a time, a minimum gap between
    /// calls, and a hard cap on calls per run.
    /// </summary>
    private async Task<HttpResponseMessage> PacedPostAsync(string model, HttpContent content, CancellationToken ct)
    {
        await CallGate.WaitAsync(ct);
        try
        {
            if (++_callsThisRun > MaxCallsPerRun)
                throw new GeminiQuotaException(
                    $"Stopping: this run reached its budget of {MaxCallsPerRun} Gemini calls (GEMINI_MAX_CALLS_PER_RUN), " +
                    "set to stay well inside the free tier. Nothing is published from an unfinished run.");

            var sinceLast = DateTime.UtcNow - _lastCallUtc;
            if (sinceLast < MinGap)
                await Task.Delay(MinGap - sinceLast, ct);

            var response = await _httpClient.PostAsync($"v1beta/models/{model}:generateContent", content, ct);
            _lastCallUtc = DateTime.UtcNow;
            return response;
        }
        finally
        {
            CallGate.Release();
        }
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

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

    // Gemini reports which quota was hit; per-day quotas have "PerDay" in their id.
    [System.Text.RegularExpressions.GeneratedRegex(@"PerDay|per day", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DailyQuota();

    // "retryDelay": "23s" in the error details.
    [System.Text.RegularExpressions.GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""")]
    private static partial System.Text.RegularExpressions.Regex RetryDelay();

    private sealed class TopicProposal
    {
        public string Title { get; set; } = string.Empty;
        public string Angle { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<int> SourceIndexes { get; set; } = new();
    }
}

/// <summary>
/// The Gemini project can't be billed (HTTP 402). Deliberately not an InvalidOperationException, so the model and
/// fact-checker fallbacks don't swallow it: no other model can succeed either.
/// </summary>
public sealed class GeminiBillingException(string message) : Exception(message);

/// <summary>
/// A free-tier limit was reached (daily quota, or the bot's own per-run call budget). Like billing errors, this stops
/// the run instead of being retried by the model fallbacks.
/// </summary>
public sealed class GeminiQuotaException(string message) : Exception(message);
