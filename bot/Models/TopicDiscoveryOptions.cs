namespace BlogBot.Models;

public sealed class TopicDiscoveryOptions
{
    /// <summary>
    /// Stack Exchange site parameter, e.g. "stackoverflow".
    /// </summary>
    public string StackExchangeSite { get; set; } = "stackoverflow";

    /// <summary>
    /// Semicolon separated tags for Stack Overflow, e.g. ".net;c#".
    /// </summary>
    public string StackOverflowTags { get; set; } = ".net;c#";

    /// <summary>
    /// Optional Stack Exchange API key to increase quota.
    /// See https://api.stackexchange.com/docs for details.
    /// </summary>
    public string? StackExchangeKey { get; set; }

    /// <summary>
    /// How many Stack Overflow questions to fetch per run (max 100).
    /// </summary>
    public int StackOverflowPageSize { get; set; } = 20;

    /// <summary>
    /// Dev.to tag to filter by, e.g. "dotnet".
    /// </summary>
    public string DevToTag { get; set; } = "dotnet";

    /// <summary>
    /// How many Dev.to articles to fetch per run (1..1000 per docs).
    /// </summary>
    public int DevToPageSize { get; set; } = 20;

    /// <summary>
    /// Subreddit name to query, e.g. "dotnet".
    /// </summary>
    public string RedditSubreddit { get; set; } = "dotnet";

    /// <summary>
    /// How many Reddit posts to fetch per run (Reddit default max is 100).
    /// </summary>
    public int RedditLimit { get; set; } = 20;

    /// <summary>
    /// Maximum number of combined candidates to return (after merging + sorting).
    /// </summary>
    public int MaxCombinedCandidates { get; set; } = 50;
}
