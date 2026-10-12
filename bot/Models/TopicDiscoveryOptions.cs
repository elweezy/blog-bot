namespace BlogBot.Models;

public sealed class TopicDiscoveryOptions
{
    /// <summary>
    /// Only consider items published within this many days.
    /// </summary>
    public int LookbackDays { get; set; } = 7;

    /// <summary>
    /// Hacker News (Algolia API) search queries; results are merged and de-duplicated.
    /// </summary>
    public List<string> HackerNewsQueries { get; set; } = ["LLM", "AI agent", "RAG", "inference", "fine-tuning", "evals"];

    /// <summary>
    /// Minimum HN points for a story to be considered.
    /// </summary>
    public int HackerNewsMinPoints { get; set; } = 30;

    public int HackerNewsPerQuery { get; set; } = 10;

    /// <summary>
    /// Dev.to tags to pull top articles from.
    /// </summary>
    public List<string> DevToTags { get; set; } = ["ai", "llm", "machinelearning"];

    public int DevToPerTag { get; set; } = 15;

    /// <summary>
    /// Subreddits read through their public RSS feed (the JSON API blocks unauthenticated clients).
    /// </summary>
    public List<string> Subreddits { get; set; } = ["LocalLLaMA", "MachineLearning"];

    public int RedditLimit { get; set; } = 15;

    public bool IncludeHuggingFacePapers { get; set; } = true;

    public int HuggingFaceLimit { get; set; } = 15;

    /// <summary>
    /// Maximum number of combined candidates sent to the model.
    /// </summary>
    public int MaxCombinedCandidates { get; set; } = 60;
}
