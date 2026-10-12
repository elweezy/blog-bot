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

        text = LimitEmDashes(text);
        return text.Trim() + "\n";
    }

    public List<Issue> FindIssues(string markdown)
    {
        var issues = new List<Issue>();
        var prose = ProseOnly(markdown);

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

    public static string ProseOnly(string markdown) => FencedBlock().Replace(markdown, " ");

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

    [GeneratedRegex(@"\s*—\s*")]
    private static partial Regex EmDash();

    [GeneratedRegex(@"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]")]
    private static partial Regex Emoji();
}
