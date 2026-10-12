using System.Text;
using System.Text.RegularExpressions;

namespace BlogBot.Services;

/// <summary>
/// Deterministic checks and cleanup for generated posts: catches invented personal experience,
/// common AI tells and missing structure, and fixes mechanical formatting problems.
/// </summary>
public sealed partial class PostSanitizer
{
    public sealed record Issue(string Message, bool Blocking);

    private const int MaxEmDashes = 2;

    // Claims of personal experience the model cannot have. "anecdote" alone is enough:
    // the model uses it to refer back to stories it never told.
    private static readonly Regex[] InventedExperience =
    [
        Rx(@"\banecdote\b"),
        Rx(@"\bI (still )?remember\b"),
        Rx(@"\bI recall\b"),
        Rx(@"\bI once\b"),
        Rx(@"\bwhen I was\b"),
        Rx(@"\b(early|earlier) in my career\b"),
        Rx(@"\bin my (experience|career|last job|previous (job|role|company))\b"),
        Rx(@"\bat my (company|job|work|day job|last|previous)\b"),
        Rx(@"\b(my|our) team\b"),
        Rx(@"\ba (client|customer|colleague|coworker) (of (mine|ours)|once|told me)\b"),
        Rx(@"\blast (week|month|year|quarter|sprint)\b,? (I|we)\b"),
        Rx(@"\bwe (shipped|deployed|migrated|built|had to|spent|learned|ran into)\b"),
        Rx(@"\bI(['’]ve| have) (seen|watched|spent|shipped|built|debugged|been burned|lost count|worked)\b"),
        Rx(@"\bI learned (this )?the hard way\b"),
        Rx(@"\bwar stor(y|ies)\b"),
        Rx(@"\b(years|months) ago,? (I|we)\b"),
        Rx(@"\b(a few|some|several|many|two|three) (years|months|weeks) (back|ago),? (I|we|my|our)\b"),
        Rx(@"\bfirst[- ]?hand\b"),
        Rx(@"\bI(['’]ve| have) (witnessed|lived|been there|run into|hit this|fought)\b"),
        Rx(@"\bI was (wrestling|working on|debugging|building|tasked|called|paged|on call)\b"),
        Rx(@"\bI (chased|spent|lost|fought|wrestled|got paged|was paged)\b"),
        Rx(@"\b(the|my) (phone|pager) (buzzed|rang|went off)\b"),
        Rx(@"\bour (customer|client|production|prod|legacy|internal|main) (portal|app|application|service|system|api|database|cluster)\b"),
        Rx(@"\bwe(['’]ve| have) all been there\b"),
        Rx(@"\bI know I have\b"),
        Rx(@"\bmore times than I(['’]d| would)? care to admit\b")
    ];

    private static readonly (Regex Pattern, string Label)[] BannedPhrases =
    [
        (Rx(@"\bdelv(e|es|ing)\b"), "delve"),
        (Rx(@"\btapestry\b"), "tapestry"),
        (Rx(@"\blandscape\b"), "landscape"),
        (Rx(@"\bin today['’]s (fast-paced|rapidly|ever)"), "in today's fast-paced"),
        (Rx(@"\bgame[- ]chang(er|ing)\b"), "game-changer"),
        (Rx(@"\bunlock(s|ing)?\b"), "unlock"),
        (Rx(@"\bleverag(e|es|ing)\b"), "leverage"),
        (Rx(@"\brobust\b"), "robust"),
        (Rx(@"\bseamless(ly)?\b"), "seamless"),
        (Rx(@"\bit(['’]s| is) (important|worth) (to note|noting)\b"), "it's important to note"),
        (Rx(@"\bin conclusion\b"), "in conclusion"),
        (Rx(@"(^|\n)\s*(Moreover|Furthermore|Additionally),"), "Moreover/Furthermore opener"),
        (Rx(@"\bever[- ]evolving\b"), "ever-evolving"),
        (Rx(@"\btestament to\b"), "testament to"),
        (Rx(@"\brealm\b"), "realm"),
        (Rx(@"\bharness(ing)? the power\b"), "harness the power"),
        (Rx(@"\bembark\b"), "embark"),
        (Rx(@"\bparadigm shift\b"), "paradigm shift"),
        (Rx(@"\bnavigat(e|ing) the (complex|world|challenges)"), "navigate the ..."),
        (Rx(@"\blet['’]s dive in\b"), "let's dive in")
    ];

    // One token set per listed experience; a flagged sentence must closely match one of them.
    private readonly List<HashSet<string>> _experiences;

    /// <param name="authorExperiences">The "Real experiences" part of the author profile; sentences that match it are allowed.</param>
    public PostSanitizer(string? authorExperiences = null)
    {
        _experiences = (authorExperiences ?? string.Empty)
            .Split('\n')
            .Select(l => TopicSimilarity.Tokens(l.TrimStart('-', '*', ' ')))
            .Where(t => t.Count >= 3)
            .ToList();
    }

    public string Clean(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Trim().TrimStart('﻿');

        // Unwrap a response that was wrapped in a single ```markdown fence.
        var wrapped = WholeDocumentFence().Match(text);
        if (wrapped.Success)
            text = wrapped.Groups[1].Value.Trim();

        // The layout already prints the title.
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].StartsWith("# "))
            text = string.Join('\n', lines.Skip(1)).TrimStart();

        // Quotes inside citation markers must stay exactly as in the source, so cleanup works around them.
        var saved = new List<string>();
        text = Citations.Protect(Citations.FixPlacement(text), saved);
        text = LimitEmDashes(text);
        text = StripHtmlAndLinks(text);
        return Citations.Restore(text, saved).Trim() + "\n";
    }

    /// <param name="sourceMaterial">When given, every metric in the post (prices, latencies, percentages, sizes) must appear in it.</param>
    public List<Issue> FindIssues(string markdown, string? sourceMaterial = null)
    {
        var issues = new List<Issue>();
        var prose = ProseOnly(markdown);

        if (sourceMaterial is not null)
        {
            foreach (var claim in UnsupportedNumbers(prose, sourceMaterial))
            {
                issues.Add(new Issue(
                    $"The figure \"{claim}\" does not appear in the sources. Remove it, replace it with a figure the " +
                    "sources state, or describe it qualitatively. Numbers are only allowed without a source inside an " +
                    "explicit assumption (\"Assume ...\").",
                    Blocking: true));
            }
        }

        if (FirstPersonOpinion().Matches(prose).Count < 2)
            issues.Add(new Issue(
                "Almost no first-person opinion. Add two or three places where the author takes a clear position " +
                "(\"I'd ...\", \"I think ...\", \"I'm not convinced ...\"), without inventing any experience.",
                Blocking: false));

        foreach (var sentence in Sentences().Split(prose))
        {
            var hit = InventedExperience.FirstOrDefault(r => r.IsMatch(sentence));
            if (hit is null || IsBackedByProfile(sentence)) continue;

            issues.Add(new Issue(
                $"Invented personal experience (\"{hit.Match(sentence).Value}\") in: \"{sentence.Trim()}\". " +
                "Rewrite it as a general statement, a claim from the sources, or an explicit opinion.",
                Blocking: true));
        }

        if (MermaidBlocks(markdown).Count == 0)
            issues.Add(new Issue("No ```mermaid diagram. Add at least one diagram that shows the mechanism.", Blocking: true));

        if (!DraftDesignHeading().IsMatch(markdown))
            issues.Add(new Issue("Missing the \"## Draft design\" section.", Blocking: true));

        foreach (var (pattern, label) in BannedPhrases)
        {
            if (pattern.IsMatch(prose))
                issues.Add(new Issue($"Banned phrase \"{label}\". Use plain wording instead.", Blocking: false));
        }

        if (Emoji().IsMatch(prose))
            issues.Add(new Issue("Contains emoji. Remove them.", Blocking: false));

        return issues;
    }

    public static IReadOnlyList<string> BannedPhraseLabels => BannedPhrases.Select(b => b.Label).ToList();

    public static List<Match> MermaidBlocks(string markdown) => MermaidBlock().Matches(markdown).ToList();

    // Quoted source text inside citation markers isn't the post's own prose, so style checks skip it.
    public static string ProseOnly(string markdown) => Citations.StripMarkers(FencedBlock().Replace(markdown, " "));

    /// <summary>
    /// The description is the RSS summary Zapier posts to LinkedIn, so its figures must be in the sources too.
    /// </summary>
    public static List<Issue> FindTeaserIssues(string description, string sourceMaterial) =>
        UnsupportedNumbers(description, sourceMaterial)
            .Select(claim => new Issue(
                $"The description (LinkedIn teaser) uses the figure \"{claim}\", which does not appear in the sources. " +
                "Use a figure the sources state or describe it qualitatively.",
                Blocking: true))
            .ToList();

    /// <summary>
    /// Metric-like figures in the prose (money, latency, percentages, token counts, multipliers) whose numbers are not
    /// in the source text. Sentences that state an explicit assumption are exempt.
    /// </summary>
    public static List<string> UnsupportedNumbers(string prose, string sourceMaterial)
    {
        // "number|unit" pairs the sources state, e.g. "0.3|$", "261|ms", "96.9|%". Counts of things ("94 queries")
        // are phrased too many ways ("94/105", "94 of them") to require the unit, so any mention of the number counts.
        var sourceFigures = MetricClaim().Matches(sourceMaterial).SelectMany(Figures)
            .Concat(NumberToken().Matches(sourceMaterial).Select(m => NormalizeNumber(m.Value) + "|" + CountUnit))
            .ToHashSet();
        var result = new List<string>();

        foreach (var sentence in Sentences().Split(prose))
        {
            if (Assumption().IsMatch(sentence)) continue;

            foreach (Match claim in MetricClaim().Matches(sentence))
            {
                if (Figures(claim).All(sourceFigures.Contains)) continue;
                if (!result.Contains(claim.Value.Trim()))
                    result.Add(claim.Value.Trim());
            }
        }

        return result;
    }

    /// <summary>Number of checkable figures (a number with a unit or currency) in the text.</summary>
    public static int CountFigures(string text) => MetricClaim().Count(text);

    private static IEnumerable<string> Figures(Match claim)
    {
        var unit = UnitOf(claim.Value);
        return NumberToken().Matches(claim.Value).Select(m => NormalizeNumber(m.Value) + "|" + unit);
    }

    private const string CountUnit = "#";

    private static readonly HashSet<string> CountUnits =
    [
        "item", "file", "chunk", "vector", "query", "querie", "document", "page", "user", "session", "record", "row",
        "embedding", "parameter", "param", "call", "gpu"
    ];

    private static string UnitOf(string claim)
    {
        if (claim.Contains('$')) return "$";

        var unit = TrailingUnit().Match(claim).Value.Trim().ToLowerInvariant();
        if (CountUnits.Contains(unit.TrimEnd('s'))) return CountUnit;

        return unit switch
        {
            "percent" => "%",
            "milliseconds" or "millisecond" => "ms",
            "seconds" or "second" or "secs" or "sec" or "s" => "s",
            "minutes" or "minute" => "min",
            "hours" or "hour" => "h",
            "×" => "x",
            "cent" or "cents" => "cents",
            "dollar" or "dollars" => "$",
            _ when unit.StartsWith("times") => "x",
            _ => unit.TrimEnd('s')
        };
    }

    private static string NormalizeNumber(string value)
    {
        var n = value.Replace(",", "");
        if (n.Contains('.')) n = n.TrimEnd('0').TrimEnd('.');
        return n;
    }

    private bool IsBackedByProfile(string sentence)
    {
        var tokens = TopicSimilarity.Tokens(sentence);
        return _experiences.Any(e =>
        {
            var shared = e.Count(tokens.Contains);
            return shared >= 3 && shared >= 0.7 * e.Count;
        });
    }

    private static string LimitEmDashes(string markdown)
    {
        var sb = new StringBuilder();
        var kept = 0;
        var inFence = false;

        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) inFence = !inFence;

            if (inFence || !line.Contains('—'))
            {
                sb.Append(line).Append('\n');
                continue;
            }

            var result = EmDash().Replace(line, m => ++kept <= MaxEmDashes ? m.Value : ", ");
            sb.Append(result).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Post text is generated from untrusted web content, and kramdown renders raw HTML as-is. Outside code, remove
    /// HTML tags and turn Markdown links into plain text (sources are listed separately), so a prompt-injected
    /// script or link can't reach the site.
    /// </summary>
    public static string StripHtmlAndLinks(string markdown)
    {
        var sb = new StringBuilder();
        var inFence = false;

        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) inFence = !inFence;
            if (inFence || line.TrimStart().StartsWith("```"))
            {
                sb.Append(line).Append('\n');
                continue;
            }

            // Leave inline code spans alone; clean only the text between them.
            var cleanLine = ScriptOrStyleBlock().Replace(line, "");
            var parts = InlineCode().Split(cleanLine);
            var spans = InlineCode().Matches(cleanLine);
            for (var i = 0; i < parts.Length; i++)
            {
                var part = MarkdownLink().Replace(parts[i], "$1");
                part = HtmlTag().Replace(part, "");
                sb.Append(part);
                if (i < spans.Count) sb.Append(spans[i].Value);
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex(@"^```(?:markdown|md)?\s*\n(.*)\n```$", RegexOptions.Singleline)]
    private static partial Regex WholeDocumentFence();

    [GeneratedRegex(@"```mermaid[^\n]*\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex MermaidBlock();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

    [GeneratedRegex(@"^#{2,3}\s+.*draft design", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DraftDesignHeading();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex Sentences();

    // A figure with a unit or currency, optionally as a range: "$0.30", "2 to 5 seconds", "200-400 ms", "96.9%", "10x",
    // or a count of things ("100,000 vectors", "250k items", "16 sessions").
    [GeneratedRegex(@"(\$\s?\d[\d,]*(\.\d+)?(\s?(to|-|–)\s?\$?\d[\d,]*(\.\d+)?)?(\s?(million|billion|k|m|bn)\b)?)|(\b\d[\d,]*(\.\d+)?(\s?(to|-|–)\s?\d[\d,]*(\.\d+)?)?([kmb](?=\s))?\s?(%|percent\b|ms\b|milliseconds?\b|seconds?\b|secs?\b|minutes?\b|hours?\b|x\b|×|times (faster|cheaper|slower|more|less)|million\b|billion\b|[kmb] tokens\b|tokens\b|requests\b|rps\b|qps\b|gb\b|mb\b|kb\b|tb\b|s\b|fps\b|-d\b|cents?\b|dollars?\b|items?\b|files?\b|chunks?\b|vectors?\b|quer(y|ies)\b|documents?\b|pages?\b|users?\b|sessions?\b|records?\b|rows?\b|embeddings?\b|parameters\b|params\b|calls\b|gpus?\b))", RegexOptions.IgnoreCase)]
    private static partial Regex MetricClaim();

    [GeneratedRegex(@"\d[\d,]*(\.\d+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"[^\d\s,.\-–]+[a-z ]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingUnit();

    [GeneratedRegex(@"\b(assume|assuming|assumption|suppose|say we|imagine|for example, a|hypothetical)", RegexOptions.IgnoreCase)]
    private static partial Regex Assumption();

    [GeneratedRegex(@"\bI(['’]d|['’]m| think| would| wouldn['’]t| prefer| suspect| expect| doubt| believe| bet| lean| want)\b")]
    private static partial Regex FirstPersonOpinion();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    // [text](url) and ![alt](url), allowing one level of parentheses inside the URL.
    [GeneratedRegex(@"!?\[([^\]\n]*)\]\((?:[^()\n]|\([^()\n]*\))*\)")]
    private static partial Regex MarkdownLink();

    // Script and style elements go with their contents, not just their tags.
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyleBlock();

    // Real tags and autolinks (<script>, </div>, <img ...>, <!-- -->, <https://...>), not "a < b" comparisons.
    [GeneratedRegex(@"<(/?[a-zA-Z][a-zA-Z0-9-]*(\s[^<>]*)?/?|!--.*?--|https?://[^>\s]+)>", RegexOptions.Singleline)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s*—\s*")]
    private static partial Regex EmDash();

    [GeneratedRegex(@"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]")]
    private static partial Regex Emoji();
}
