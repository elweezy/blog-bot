using System.Text.RegularExpressions;

namespace BlogBot.Utils;

public static partial class Slug
{
    public static string From(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "post";

        text = text.ToLowerInvariant()
            .Replace("c#", "csharp")
            .Replace(".net", "dotnet")
            .Replace("+", "plus");

        text = InvalidChars().Replace(text, "");
        text = Separators().Replace(text, "-");
        text = Hyphens().Replace(text, "-").Trim('-');

        if (text.Length > 80)
            text = text[..80].TrimEnd('-');

        return string.IsNullOrEmpty(text) ? "post" : text;
    }

    [GeneratedRegex(@"[^a-z0-9\s_-]")]
    private static partial Regex InvalidChars();

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Separators();

    [GeneratedRegex("-{2,}")]
    private static partial Regex Hyphens();
}
