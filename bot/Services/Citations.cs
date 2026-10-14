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
        MarkerAfterPunctuation().Replace(SplitMultiQuote(markdown), m => $" {m.Groups[2].Value.Trim()}{m.Groups[1].Value}");

    /// <summary>
    /// Models sometimes put several quotes in one marker: [[S1: "a", "b"]]. Split it into [[S1: "a"]] [[S1: "b"]].
    /// </summary>
    private static string SplitMultiQuote(string markdown) =>
        MultiQuoteMarker().Replace(markdown, m =>
        {
            var source = m.Groups[1].Value;
            var quotes = QuotedString().Matches(m.Groups[2].Value).Select(q => q.Groups[1].Value);
            return string.Join(" ", quotes.Select(q => $"[[S{source}: \"{q}\"]]"));
        });

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
    public static string RemoveClaims(string markdown, IReadOnlyCollection<Claim> rejected) =>
        ReplaceClaims(markdown, rejected.ToDictionary(c => c.Text, _ => string.Empty));

    /// <summary>
    /// Replaces units whose stripped text is a key with the given text (empty removes the unit). Leading whitespace,
    /// list markers and line breaks are kept so paragraphs, lists and tables keep their shape.
    /// </summary>
    public static string ReplaceClaims(string markdown, IReadOnlyDictionary<string, string> replacements)
    {
        if (replacements.Count == 0) return markdown;

        var sb = new StringBuilder();
        foreach (var unit in Units(markdown))
        {
            var key = StripMarkers(unit.Text).Trim();
            if (unit.InCode || !replacements.TryGetValue(key, out var replacement))
            {
                sb.Append(unit.Text);
                continue;
            }

            var leading = unit.Text[..(unit.Text.Length - unit.Text.TrimStart().Length)];
            var newline = unit.Text.EndsWith('\n') ? "\n" : string.Empty;
            if (replacement.Length == 0)
            {
                sb.Append(newline);
                continue;
            }

            // A list item keeps its marker even if the model returned the sentence without it.
            var listMarker = ListItem().Match(key);
            if (listMarker.Success && !ListItem().IsMatch(replacement))
                replacement = listMarker.Value.TrimStart() + replacement;

            sb.Append(leading).Append(replacement.Trim()).Append(newline);
        }

        // Drop list markers left empty and collapse the blank lines that removal leaves behind.
        var text = Regex.Replace(sb.ToString(), @"^[ \t]*([-*]|\d+\.)[ \t]*$\n?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return TrimParagraphStarts(text).Trim() + "\n";
    }

    /// <summary>
    /// Removing a paragraph's first sentence leaves the next one starting with a space. Trim that at paragraph
    /// starts only; code, list items, tables and real indentation are left alone.
    /// </summary>
    private static string TrimParagraphStarts(string markdown)
    {
        var lines = markdown.Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```")) inFence = !inFence;
            if (inFence) continue;

            var startsParagraph = i == 0 || lines[i - 1].Trim().Length == 0;
            var indent = lines[i].Length - trimmed.Length;
            if (startsParagraph && indent is > 0 and < 4 && !ListItem().IsMatch(lines[i]) && !trimmed.StartsWith('|'))
                lines[i] = trimmed;
        }

        return string.Join('\n', lines);
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

    [GeneratedRegex("""\[\[S(\d+):\s*((?:"[^"\n]*"\s*,\s*)+"[^"\n]*")\s*\]\]""")]
    private static partial Regex MultiQuoteMarker();

    [GeneratedRegex("\"([^\"\n]*)\"")]
    private static partial Regex QuotedString();

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
