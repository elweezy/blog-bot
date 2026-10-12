using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BlogBot.Models;
using Microsoft.Extensions.Options;

namespace BlogBot.Services;

/// <summary>
/// Fetches recent AI engineering discussions from free, key-less sources (Hacker News, Dev.to,
/// Reddit RSS, Hugging Face daily papers) and maps them into TopicCandidate instances.
/// Also fetches the readable text of chosen sources so the writer works from real material.
/// </summary>
public sealed partial class TopicDiscoveryService
{
    private const int SummaryLength = 280;

    private readonly HttpClient _httpClient;
    private readonly TopicDiscoveryOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TopicDiscoveryService(HttpClient httpClient, IOptions<TopicDiscoveryOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<List<TopicCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        var tasks = new Dictionary<string, Task<List<TopicCandidate>>>
        {
            ["hackernews"] = FetchHackerNewsAsync(ct),
            ["devto"] = FetchDevToAsync(ct),
            ["reddit"] = FetchAllRedditAsync(ct)
        };
        if (_options.IncludeHuggingFacePapers)
            tasks["huggingface"] = FetchHuggingFacePapersAsync(ct);

        await Task.WhenAll(tasks.Values);

        foreach (var (name, task) in tasks)
            Console.WriteLine($"Source {name}: {task.Result.Count} candidates");

        var results = tasks.Values.Select(t => t.Result);

        // Score = popularity within its source, decayed by age so fresh discussions win.
        return results
            .SelectMany(r => r)
            .Where(c => !string.IsNullOrWhiteSpace(c.Title) && !string.IsNullOrWhiteSpace(c.Url))
            .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Popularity).First())
            .OrderByDescending(Score)
            .Take(_options.MaxCombinedCandidates)
            .ToList();
    }

    public static double Score(TopicCandidate c)
    {
        var ageDays = Math.Max(0, (DateTime.UtcNow - c.CreatedAt).TotalDays);
        return c.Popularity * Math.Exp(-ageDays / 7.0);
    }

    /// <summary>
    /// Returns readable text for a source (article body, HN post + top comments, paper abstract), capped in length.
    /// </summary>
    public async Task<string> FetchSourceTextAsync(TopicCandidate candidate, int maxChars, CancellationToken ct = default)
    {
        try
        {
            var text = candidate.Source switch
            {
                "devto" when candidate.SourceId is not null => await FetchDevToBodyAsync(candidate.SourceId, ct),
                "hackernews" when candidate.SourceId is not null => await FetchHackerNewsThreadAsync(candidate, maxChars, ct),
                "huggingface" => candidate.Summary,
                "reddit" => await FetchRedditThreadAsync(candidate.Url, maxChars, ct),
                _ => await FetchPageTextAsync(candidate.Url, ct)
            };

            // Script-rendered pages strip down to almost nothing; keep the feed summary rather than lose it.
            if (text.Trim().Length < 300)
                text = (candidate.Summary + "\n" + text).Trim();

            return Truncate(text, maxChars);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not fetch source text for {candidate.Url}: {ex.Message}");
            return candidate.Summary;
        }
    }

    #region Hacker News

    private async Task<List<TopicCandidate>> FetchHackerNewsAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-_options.LookbackDays).ToUnixTimeSeconds();
        var hits = new Dictionary<string, HnHit>();

        foreach (var query in _options.HackerNewsQueries)
        {
            var uri = "https://hn.algolia.com/api/v1/search" +
                      $"?query={Uri.EscapeDataString(query)}" +
                      "&tags=story" +
                      $"&numericFilters={Uri.EscapeDataString($"created_at_i>{since},points>{_options.HackerNewsMinPoints}")}" +
                      $"&hitsPerPage={_options.HackerNewsPerQuery}";

            var data = await GetJsonAsync<HnSearchResponse>(uri, "Hacker News", ct);
            foreach (var hit in data?.Hits ?? [])
                hits.TryAdd(hit.ObjectId, hit);
        }

        if (hits.Count == 0) return [];

        var maxPoints = Math.Max(1, hits.Values.Max(h => h.Points));
        return hits.Values.Select(h =>
        {
            var discussion = $"https://news.ycombinator.com/item?id={h.ObjectId}";
            return new TopicCandidate
            {
                Title = h.Title,
                Source = "hackernews",
                Url = string.IsNullOrWhiteSpace(h.Url) ? discussion : h.Url,
                DiscussionUrl = discussion,
                SourceId = h.ObjectId,
                Summary = Truncate(StripHtml(h.StoryText ?? string.Empty), SummaryLength),
                Popularity = (double)h.Points / maxPoints,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(h.CreatedAtI).UtcDateTime
            };
        }).ToList();
    }

    private async Task<string> FetchHackerNewsThreadAsync(TopicCandidate candidate, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // The linked article carries the substance; the thread adds practitioner pushback.
        if (candidate.DiscussionUrl is not null && candidate.Url != candidate.DiscussionUrl)
        {
            // A slow or blocked article site must not cost us the thread as well.
            try
            {
                sb.AppendLine(Truncate(await FetchPageTextAsync(candidate.Url, ct), maxChars * 2 / 3)).AppendLine();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Could not fetch article {candidate.Url}: {ex.Message}; using the HN thread only.");
            }
        }

        var item = await GetJsonAsync<HnItem>($"https://hn.algolia.com/api/v1/items/{candidate.SourceId}", "Hacker News item", ct);
        if (item is null) return sb.ToString();

        if (!string.IsNullOrWhiteSpace(item.Text))
            sb.AppendLine(StripHtml(item.Text)).AppendLine();

        var comments = item.Children
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Take(6)
            .Select(c => "- HN commenter: " + Truncate(StripHtml(c.Text!), 500));
        sb.AppendLine("Discussion highlights:").AppendLine(string.Join("\n", comments));

        return sb.ToString();
    }

    private sealed class HnSearchResponse
    {
        [JsonPropertyName("hits")] public List<HnHit> Hits { get; set; } = new();
    }

    private sealed class HnHit
    {
        [JsonPropertyName("objectID")] public string ObjectId { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("points")] public int Points { get; set; }
        [JsonPropertyName("created_at_i")] public long CreatedAtI { get; set; }
        [JsonPropertyName("story_text")] public string? StoryText { get; set; }
    }

    private sealed class HnItem
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("children")] public List<HnItem> Children { get; set; } = new();
    }

    #endregion

    #region Dev.to

    private async Task<List<TopicCandidate>> FetchDevToAsync(CancellationToken ct)
    {
        var articles = new Dictionary<long, DevToArticle>();

        foreach (var tag in _options.DevToTags)
        {
            var uri = "https://dev.to/api/articles" +
                      $"?tag={Uri.EscapeDataString(tag)}" +
                      $"&top={_options.LookbackDays}" +
                      $"&per_page={_options.DevToPerTag}";

            var data = await GetJsonAsync<List<DevToArticle>>(uri, "Dev.to", ct);
            foreach (var a in data ?? [])
                articles.TryAdd(a.Id, a);
        }

        if (articles.Count == 0) return [];

        var maxReactions = Math.Max(1, articles.Values.Max(a => a.PublicReactionsCount));
        return articles.Values.Select(a => new TopicCandidate
        {
            Title = a.Title,
            Source = "devto",
            Url = a.Url,
            SourceId = a.Id.ToString(),
            Summary = Truncate(a.Description ?? string.Empty, SummaryLength),
            Popularity = (double)a.PublicReactionsCount / maxReactions,
            CreatedAt = a.PublishedAt ?? DateTime.UtcNow
        }).ToList();
    }

    private async Task<string> FetchDevToBodyAsync(string id, CancellationToken ct)
    {
        var article = await GetJsonAsync<DevToArticle>($"https://dev.to/api/articles/{id}", "Dev.to article", ct);
        return article?.BodyMarkdown ?? string.Empty;
    }

    private sealed class DevToArticle
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("public_reactions_count")] public int PublicReactionsCount { get; set; }
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("body_markdown")] public string? BodyMarkdown { get; set; }
    }

    #endregion

    #region Reddit (RSS)

    // One subreddit at a time: Reddit answers parallel feed requests with 429.
    private async Task<List<TopicCandidate>> FetchAllRedditAsync(CancellationToken ct)
    {
        var all = new List<TopicCandidate>();
        for (var i = 0; i < _options.Subreddits.Count; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3), ct);
            all.AddRange(await FetchRedditAsync(_options.Subreddits[i], ct));
        }

        return all;
    }

    private async Task<List<TopicCandidate>> FetchRedditAsync(string subreddit, CancellationToken ct)
    {
        var uri = $"https://www.reddit.com/r/{Uri.EscapeDataString(subreddit.Trim('/'))}/top/.rss" +
                  $"?t=week&limit={_options.RedditLimit}";

        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Reddit r/{subreddit} returned {(int)response.StatusCode}; skipping.");
                return [];
            }

            var doc = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var entries = doc.Descendants(atom + "entry").ToList();

            // The feed is already ordered by score; use rank as popularity.
            return entries.Select((e, i) => new TopicCandidate
            {
                Title = e.Element(atom + "title")?.Value ?? string.Empty,
                Source = "reddit",
                Url = e.Element(atom + "link")?.Attribute("href")?.Value ?? string.Empty,
                Summary = Truncate(StripHtml(e.Element(atom + "content")?.Value ?? string.Empty), SummaryLength),
                Popularity = 1.0 - (double)i / Math.Max(1, entries.Count),
                CreatedAt = DateTime.TryParse((e.Element(atom + "published") ?? e.Element(atom + "updated"))?.Value, out var d)
                    ? d.ToUniversalTime()
                    : DateTime.UtcNow
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error fetching Reddit r/{subreddit}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Reddit pages are rendered by JavaScript, so the HTML has no text. The thread's RSS feed has the full post
    /// followed by its comments.
    /// </summary>
    private async Task<string> FetchRedditThreadAsync(string url, int maxChars, CancellationToken ct)
    {
        var xml = await GetWithRateLimitRetryAsync(url.TrimEnd('/') + "/.rss", ct);
        if (xml is null) return string.Empty;

        var doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var texts = doc.Descendants(atom + "entry")
            .Select(e => StripRedditFooter(StripHtml(e.Element(atom + "content")?.Value ?? string.Empty)))
            .Where(t => t.Length > 0)
            .ToList();
        if (texts.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine(Truncate(texts[0], maxChars * 2 / 3)).AppendLine();

        var comments = texts.Skip(1)
            .Where(t => t.Length > 80 && !t.Contains("I am a bot", StringComparison.OrdinalIgnoreCase))
            .Take(8).Select(t => "- Reddit commenter: " + Truncate(t, 500));
        sb.AppendLine("Discussion highlights:").AppendLine(string.Join("\n", comments));
        return sb.ToString();
    }

    // Reddit rate-limits feed requests aggressively; wait as long as it asks (capped) and try again.
    private async Task<string?> GetWithRateLimitRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 3)
            {
                Console.Error.WriteLine($"Reddit thread {url} returned {(int)response.StatusCode}.");
                return null;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * attempt);
            if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            Console.Error.WriteLine($"Reddit rate limit; retrying in {wait.TotalSeconds:0}s.");
            await Task.Delay(wait, ct);
        }

        return null;
    }

    // Every RSS entry ends with "submitted by /u/... [link] [comments]".
    private static string StripRedditFooter(string text)
    {
        var i = text.LastIndexOf("submitted by", StringComparison.Ordinal);
        return (i > 0 ? text[..i] : text).Trim();
    }

    #endregion

    #region Hugging Face daily papers

    private async Task<List<TopicCandidate>> FetchHuggingFacePapersAsync(CancellationToken ct)
    {
        var data = await GetJsonAsync<List<HfDailyPaper>>(
            $"https://huggingface.co/api/daily_papers?limit={_options.HuggingFaceLimit}", "Hugging Face", ct);
        if (data is null || data.Count == 0) return [];

        var maxUpvotes = Math.Max(1, data.Max(p => p.Paper?.Upvotes ?? 0));
        return data.Where(p => p.Paper is not null).Select(p => new TopicCandidate
        {
            Title = p.Title ?? p.Paper!.Title ?? string.Empty,
            Source = "huggingface",
            Url = $"https://huggingface.co/papers/{p.Paper!.Id}",
            SourceId = p.Paper.Id,
            Summary = p.Paper.Summary ?? string.Empty,
            Popularity = (double)(p.Paper.Upvotes) / maxUpvotes,
            CreatedAt = p.PublishedAt ?? DateTime.UtcNow
        }).ToList();
    }

    private sealed class HfDailyPaper
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("paper")] public HfPaper? Paper { get; set; }
    }

    private sealed class HfPaper
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("upvotes")] public int Upvotes { get; set; }
    }

    #endregion

    #region Helpers

    private async Task<T?> GetJsonAsync<T>(string uri, string sourceName, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"{sourceName} returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return default;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error fetching {sourceName}: {ex.Message}");
            return default;
        }
    }

    private async Task<string> FetchPageTextAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var response = await _httpClient.GetAsync(url, timeout.Token);
        if (!response.IsSuccessStatusCode) return string.Empty;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!mediaType.Contains("html") && !mediaType.StartsWith("text/")) return string.Empty;

        var html = await response.Content.ReadAsStringAsync(timeout.Token);
        html = NonContentBlocks().Replace(html, " ");
        return StripHtml(html);
    }

    private static string StripHtml(string html)
    {
        var text = Tags().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@"<(script|style|nav|header|footer|svg|noscript)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NonContentBlocks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    #endregion
}
