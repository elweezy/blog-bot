using System.Text.Json;
using System.Text.Json.Serialization;
using BlogBot.Models;
using Microsoft.Extensions.Options;

namespace BlogBot.Services;

/// <summary>
/// Fetches real .NET-related content from Stack Overflow, Dev.to, and Reddit
/// and maps them into TopicCandidate instances.
/// </summary>
public sealed class TopicDiscoveryService
{
    private readonly HttpClient _httpClient;
    private readonly TopicDiscoveryOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public TopicDiscoveryService(HttpClient httpClient, IOptions<TopicDiscoveryOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<List<TopicCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        var candidates = new List<TopicCandidate>();

        var stackOverflowTask = FetchStackOverflowAsync(ct);
        var devToTask = FetchDevToAsync(ct);
        var redditTask = FetchRedditAsync(ct);

        await Task.WhenAll(stackOverflowTask, devToTask, redditTask);

        candidates.AddRange(stackOverflowTask.Result);
        candidates.AddRange(devToTask.Result);
        candidates.AddRange(redditTask.Result);

        // Sort by popularity and keep the top N
        return candidates
            .OrderByDescending(c => c.Popularity)
            .Take(_options.MaxCombinedCandidates)
            .ToList();
    }

    #region Stack Overflow

    private async Task<List<TopicCandidate>> FetchStackOverflowAsync(CancellationToken ct)
    {
        // Official Stack Exchange API /questions endpoint.:contentReference[oaicite:3]{index=3}
        var uri = $"https://api.stackexchange.com/2.3/questions" +
                  $"?order=desc" +
                  $"&sort=activity" +
                  $"&tagged={Uri.EscapeDataString(_options.StackOverflowTags)}" +
                  $"&site={Uri.EscapeDataString(_options.StackExchangeSite)}" +
                  $"&pagesize={_options.StackOverflowPageSize}";

        if (!string.IsNullOrWhiteSpace(_options.StackExchangeKey))
        {
            uri += $"&key={Uri.EscapeDataString(_options.StackExchangeKey)}";
        }

        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"StackOverflow API returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return new List<TopicCandidate>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<StackOverflowResponse>(json, _jsonOptions);
            if (data?.Items == null || data.Items.Count == 0)
            {
                return new List<TopicCandidate>();
            }

            var maxScore = data.Items.Max(q => q.Score);
            if (maxScore <= 0) maxScore = 1;

            return data.Items.Select(q => new TopicCandidate
            {
                Title = q.Title,
                Source = "stackoverflow",
                Url = q.Link,
                Popularity = (double)q.Score / maxScore,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(q.CreationDate).UtcDateTime
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error fetching StackOverflow data: {ex.Message}");
            return new List<TopicCandidate>();
        }
    }

    private sealed class StackOverflowResponse
    {
        [JsonPropertyName("items")] public List<QuestionItem> Items { get; set; } = new();
    }

    private sealed class QuestionItem
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

        [JsonPropertyName("link")] public string Link { get; set; } = string.Empty;

        [JsonPropertyName("score")] public int Score { get; set; }

        [JsonPropertyName("creation_date")] public long CreationDate { get; set; }
    }

    #endregion

    #region Dev.to

    private async Task<List<TopicCandidate>> FetchDevToAsync(CancellationToken ct)
    {
        // Dev.to / Forem articles endpoint with tag + per_page + state.:contentReference[oaicite:4]{index=4}
        var uri = $"https://dev.to/api/articles" +
                  $"?tag={Uri.EscapeDataString(_options.DevToTag)}" +
                  $"&per_page={_options.DevToPageSize}" +
                  $"&state=fresh";

        try
        {
            using var response = await _httpClient.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Dev.to API returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return new List<TopicCandidate>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var articles = JsonSerializer.Deserialize<List<DevToArticle>>(json, _jsonOptions);
            if (articles == null || articles.Count == 0)
            {
                return new List<TopicCandidate>();
            }

            var maxReactions = articles.Max(a => a.PublicReactionsCount);
            if (maxReactions <= 0) maxReactions = 1;

            return articles.Select(a => new TopicCandidate
            {
                Title = a.Title,
                Source = "devto",
                Url = a.Url,
                Popularity = (double)a.PublicReactionsCount / maxReactions,
                CreatedAt = a.PublishedAt ?? DateTime.UtcNow
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error fetching Dev.to data: {ex.Message}");
            return new List<TopicCandidate>();
        }
    }

    private sealed class DevToArticle
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;

        [JsonPropertyName("public_reactions_count")]
        public int PublicReactionsCount { get; set; }

        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    }

    #endregion

    #region Reddit

    private async Task<List<TopicCandidate>> FetchRedditAsync(CancellationToken ct)
    {
        // Reddit listing endpoint: /r/{subreddit}/{listing}.json with limit.:contentReference[oaicite:5]{index=5}
        var subreddit = _options.RedditSubreddit.Trim('/');
        var uri = $"https://www.reddit.com/r/{Uri.EscapeDataString(subreddit)}/hot.json?limit={_options.RedditLimit}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Reddit requires a descriptive User-Agent; set one if not already present.
            if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "blog-bot/1.0 (https://github.com/elweezy/blog-bot)");
            }

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Reddit API returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return new List<TopicCandidate>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var listing = JsonSerializer.Deserialize<RedditListingRoot>(json, _jsonOptions);
            var children = listing?.Data?.Children;
            if (children == null || children.Count == 0)
            {
                return new List<TopicCandidate>();
            }

            var maxUps = children.Max(c => c.Data.Ups);
            if (maxUps <= 0) maxUps = 1;

            const string redditBase = "https://www.reddit.com";

            return children.Select(c =>
            {
                var created = DateTimeOffset.FromUnixTimeSeconds(c.Data.CreatedUtc).UtcDateTime;
                var url = redditBase + c.Data.Permalink;

                return new TopicCandidate
                {
                    Title = c.Data.Title,
                    Source = "reddit",
                    Url = url,
                    Popularity = (double)c.Data.Ups / maxUps,
                    CreatedAt = created
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error fetching Reddit data: {ex.Message}");
            return new List<TopicCandidate>();
        }
    }

    private sealed class RedditListingRoot
    {
        [JsonPropertyName("data")] public RedditListingData? Data { get; set; }
    }

    private sealed class RedditListingData
    {
        [JsonPropertyName("children")] public List<RedditChild> Children { get; set; } = new();
    }

    private sealed class RedditChild
    {
        [JsonPropertyName("data")] public RedditPost Data { get; set; } = new();
    }

    private sealed class RedditPost
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

        [JsonPropertyName("permalink")] public string Permalink { get; set; } = string.Empty;

        [JsonPropertyName("ups")] public int Ups { get; set; }

        [JsonPropertyName("created_utc")] public long CreatedUtc { get; set; }
    }

    #endregion
}
