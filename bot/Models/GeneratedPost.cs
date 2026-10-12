namespace BlogBot.Models;

public sealed class GeneratedPost
{
    public string Markdown { get; set; } = string.Empty;

    /// <summary>
    /// 2-3 sentence teaser. Used as front matter "description", which feeds the RSS summary that Zapier posts to LinkedIn.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();
}
