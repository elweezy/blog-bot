namespace BlogBot.Models;

/// <summary>
/// The fixed set of tags used across the blog. Posts (and "Related posts" on the site) only work well when tags are
/// shared, so the model must pick from this list instead of inventing near-duplicates like "llms" or "rag-systems".
/// </summary>
public static class TagVocabulary
{
    public static readonly string[] All =
    [
        // AI engineering
        "llm", "agents", "rag", "evals", "inference", "fine-tuning", "open-models", "prompting", "ai-tooling",
        // Engineering concerns
        "architecture", "performance", "resilience", "caching", "security", "testing", "cloud-native", "real-time",
        "http", "data", "devops",
        // .NET platform (older posts)
        "dotnet", "csharp", "aspnet-core", "ef-core", "blazor"
    ];

    private static readonly HashSet<string> Known = All.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<string> Normalize(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim().ToLowerInvariant())
            .Where(Known.Contains)
            .Distinct()
            .Take(4)
            .ToList();
}
