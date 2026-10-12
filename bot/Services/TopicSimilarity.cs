using System.Text.RegularExpressions;

namespace BlogBot.Services;

/// <summary>
/// Cheap, deterministic title similarity used to block near-duplicate topics.
/// LLM titles change wording every run, so exact slug matching is not enough.
/// </summary>
public static partial class TopicSimilarity
{
    public const double DuplicateThreshold = 0.5;

    // Filler words plus terms that appear in almost every AI title and therefore say nothing about the topic.
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "beyond", "by", "can", "do", "does", "for", "from", "how", "in",
        "into", "is", "it", "its", "of", "on", "or", "over", "the", "to", "vs", "what", "when", "why", "with",
        "without", "your", "you", "we", "our", "my", "i", "this", "that", "these", "those", "real", "world",
        "guide", "deep", "dive", "practical", "modern", "build", "building", "using", "use", "production",
        "app", "apps", "application", "applications", "ai", "llm", "llms", "model", "models", "system", "systems",
        "part", "lessons", "learned", "best", "practices", "tips"
    ];

    private static readonly (string From, string To)[] Phrases =
    [
        ("c#", "csharp"),
        (".net", "dotnet"),
        ("retrieval-augmented generation", "rag"),
        ("retrieval augmented generation", "rag"),
        ("large language models", "llm"),
        ("large language model", "llm"),
        ("model context protocol", "mcp"),
        ("vector database", "vectordb"),
        ("vector databases", "vectordb"),
        ("vector store", "vectordb"),
        ("fine tuning", "finetune"),
        ("fine-tuning", "finetune"),
        ("function calling", "toolcall"),
        ("tool calling", "toolcall"),
        ("tool use", "toolcall")
    ];

    /// <summary>
    /// Returns a 0..1 score; anything at or above <see cref="DuplicateThreshold"/> is treated as the same topic.
    /// </summary>
    public static double Score(string a, string b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var overlap = ta.Intersect(tb).Count();
        var jaccard = (double)overlap / ta.Union(tb).Count();

        // Containment catches a short rephrase of a longer title ("Containerizing .NET apps" vs a long variant).
        var containment = Math.Min(ta.Count, tb.Count) >= 2
            ? (double)overlap / Math.Min(ta.Count, tb.Count)
            : 0;

        var trigram = Dice(Trigrams(string.Join(' ', ta.Order())), Trigrams(string.Join(' ', tb.Order())));

        return Math.Max(jaccard, Math.Max(containment * 0.67, trigram * 0.9));
    }

    public static bool IsDuplicate(string a, string b) => Score(a, b) >= DuplicateThreshold;

    public static HashSet<string> Tokens(string text)
    {
        text = text.ToLowerInvariant();
        foreach (var (from, to) in Phrases)
            text = text.Replace(from, to);

        return NonWord().Split(text)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Select(Stem)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToHashSet();
    }

    private static string Stem(string word)
    {
        if (word.Length <= 4) return word;

        foreach (var suffix in new[] { "ations", "ation", "ions", "ion", "ings", "ing", "ers", "er", "ies", "es", "ed", "s" })
        {
            if (word.EndsWith(suffix) && word.Length - suffix.Length >= 3)
                return suffix == "ies" ? word[..^3] + "y" : word[..^suffix.Length];
        }

        return word;
    }

    private static HashSet<string> Trigrams(string s)
    {
        var set = new HashSet<string>();
        s = $"  {s} ";
        for (var i = 0; i + 3 <= s.Length; i++)
            set.Add(s.Substring(i, 3));
        return set;
    }

    private static double Dice(HashSet<string> a, HashSet<string> b) =>
        a.Count + b.Count == 0 ? 0 : 2.0 * a.Intersect(b).Count() / (a.Count + b.Count);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();
}
