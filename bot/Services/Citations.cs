using System.Text;
using System.Text.RegularExpressions;

namespace BlogBot.Services;

/// <summary>
/// Claim-level grounding. The writer ends every factual sentence with a marker carrying the exact source text it
/// relies on: <c>[[S1: "exact words from source 1"]]</c>. This class checks that each quote really exists in its
/// source, extracts claims for verification, and deletes claims that fail. Markers are stripped before publishing;
/// attribution stays in the prose and the sources are listed under the post.
/// </summary>
public static partial class Citations
{
    public sealed record Quote(int Source, string Text);

    /// <summary>A sentence, list item or table row together with the quotes it cites.</summary>
    public sealed record Claim(int Id, string Text, IReadOnlyList<Quote> Quotes);

    /// <summary>
    /// Moves markers written after the sentence's final punctuation ("... second. [[S1: "..."]]") in front of it
    /// ("... second [[S1: "..."]]."), so every marker belongs to the sentence it supports.
    /// </summary>
    public static string FixPlacement(string markdown) =>
        MarkerAfterPunctuation().Replace(markdown, m => $" {m.Groups[2].Value.Trim()}{m.Groups[1].Value}");

    /// <summary>Replaces markers with placeholders so text cleanup can't alter the quoted words.</summary>
    public static string Protect(string markdown, List<string> saved) =>
        Marker().Replace(markdown, m =>
        {
            saved.Add(m.Value);
            return $"\u0003{saved.Count - 1}\u0004";
        });

    public static string Restore(string markdown, IReadOnlyList<string> saved) =>
        Regex.Replace(markdown, "\u0003(\\d+)\u0004", m => saved[int.Parse(m.Groups[1].Value)]);

    public static IReadOnlyList<Quote> QuotesIn(string text) =>
        Marker().Matches(text).Select(m => new Quote(int.Parse(m.Groups[1].Value), m.Groups[2].Value)).ToList();

    public static string StripMarkers(string text) => MarkerWithSpace().Replace(text, "");

    /// <summary>
    /// Quotes that are not found in the cited source. Whitespace, case, quote characters and HTML are ignored,
    /// so formatting differences don't count, but changed words do.
    /// </summary>
    public static List<string> InvalidQuotes(string markdown, IReadOnlyDictionary<int, string> sources)
    {
        var normalizedSources = sources.ToDictionary(s => s.Key, s => Normalize(s.Value));
        var invalid = new List<string>();

        foreach (var quote in QuotesIn(OutsideCode(markdown)))
        {
            var text = Normalize(quote.Text);
            var ok = text.Length >= 12 &&
                     normalizedSources.TryGetValue(quote.Source, out var source) &&
                     source.Contains(text, StringComparison.Ordinal);
            if (!ok)
                invalid.Add($"[[S{quote.Source}: \"{quote.Text}\"]]");
        }

        return invalid.Distinct().ToList();
    }

    /// <summary>
    /// Every cited unit of prose (sentence, list item or table row) outside code and diagrams.
    /// </summary>
    public static List<Claim> ExtractClaims(string markdown)
    {
        var claims = new List<Claim>();
        foreach (var unit in Units(markdown).Where(u => !u.InCode))
        {
            var quotes = QuotesIn(unit.Text);
            if (quotes.Count > 0)
                claims.Add(new Claim(claims.Count, StripMarkers(unit.Text).Trim(), quotes));
        }

        return claims;
    }

    /// <summary>
    /// Prose sentences and list items without any citation (headings, captions, tables and code excluded), so the
    /// fact-checkers can confirm they are opinion or reasoning rather than unsupported facts.
    /// </summary>
    public static List<Claim> ExtractUncited(string markdown, int firstId)
    {
        var result = new List<Claim>();
        foreach (var unit in Units(markdown).Where(u => !u.InCode))
        {
            var text = unit.Text.Trim();
            if (text.Length < 25 || text.StartsWith('#') || text.StartsWith('|') || text.StartsWith('*') ||
                QuotesIn(unit.Text).Count > 0)
                continue;
            result.Add(new Claim(firstId + result.Count, text, []));
        }

        return result;
    }

    /// <summary>
    /// Removes the units whose stripped text matches a rejected claim. Paragraphs, lists and tables keep their shape.
    /// </summary>
    public static string RemoveClaims(string markdown, IReadOnlyCollection<Claim> rejected)
    {
        if (rejected.Count == 0) return markdown;
        var rejectedTexts = rejected.Select(c => c.Text).ToHashSet();

        var sb = new StringBuilder();
        foreach (var unit in Units(markdown))
        {
            if (!unit.InCode && rejectedTexts.Contains(StripMarkers(unit.Text).Trim()))
            {
                // Keep line breaks so the surrounding structure stays intact.
                if (unit.Text.EndsWith('\n')) sb.Append('\n');
                continue;
            }

            sb.Append(unit.Text);
        }

        // Drop lines left empty mid-paragraph and collapse the blank lines that removal leaves behind.
        var text = Regex.Replace(sb.ToString(), @"^[ \t]*([-*]|\d+\.)[ \t]*$\n?", "", RegexOptions.Multiline);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim() + "\n";
    }

    public static string Normalize(string text)
    {
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = text.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-').Replace("**", "").Replace("`", "");
        return Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static string OutsideCode(string markdown) =>
        string.Concat(Units(markdown).Where(u => !u.InCode).Select(u => u.Text));

    private sealed record Unit(string Text, bool InCode);

    /// <summary>
    /// Splits Markdown into units: whole lines for code blocks, headings, list items and table rows; sentences
    /// within paragraphs. Concatenating all units gives back the original text exactly.
    /// </summary>
    private static IEnumerable<Unit> Units(string markdown)
    {
        var inFence = false;
        foreach (var rawLine in Regex.Split(markdown, @"(?<=\n)"))
        {
            if (rawLine.Length == 0) continue;
            var trimmed = rawLine.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                inFence = !inFence;
                yield return new Unit(rawLine, true);
                continue;
            }

            if (inFence || trimmed.StartsWith('#') || trimmed.StartsWith('|') || ListItem().IsMatch(rawLine) ||
                trimmed.Length == 0 || (trimmed.StartsWith('*') && trimmed.TrimEnd().EndsWith('*')))
            {
                yield return new Unit(rawLine, inFence);
                continue;
            }

            // Hide markers while splitting, since quotes can contain sentence punctuation.
            var markers = new List<string>();
            var masked = Marker().Replace(rawLine, m =>
            {
                markers.Add(m.Value);
                return $"\u0001{markers.Count - 1}\u0002";
            });

            foreach (var sentence in SentenceBoundary().Split(masked))
            {
                var restored = Placeholder().Replace(sentence, m => markers[int.Parse(m.Groups[1].Value)]);
                yield return new Unit(restored, false);
            }
        }
    }

    // [[S1: "quoted text"]]; quotes may contain anything except the closing sequence.
    [GeneratedRegex("""\[\[S(\d+):\s*"(.*?)"\s*\]\]""", RegexOptions.Singleline)]
    private static partial Regex Marker();

    [GeneratedRegex("""\s?\[\[S(\d+):\s*"(.*?)"\s*\]\]""", RegexOptions.Singleline)]
    private static partial Regex MarkerWithSpace();

    // "word. [[S1: "..."]] [[S2: "..."]]" -> punctuation, then one or more markers
    [GeneratedRegex("""([.!?])((?:\s*\[\[S\d+:\s*".*?"\s*\]\])+)""", RegexOptions.Singleline)]
    private static partial Regex MarkerAfterPunctuation();

    [GeneratedRegex(@"^\s*([-*+]|\d+\.)\s")]
    private static partial Regex ListItem();

    // Split after sentence-ending punctuation and a space, before the next sentence's first character.
    [GeneratedRegex(@"(?<=[.!?][""')\]]?)(?=\s+[A-Z0-9""'(\[*`])")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\u0001(\d+)\u0002")]
    private static partial Regex Placeholder();
}
