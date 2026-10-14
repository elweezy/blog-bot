using System.Globalization;
using System.Text;
using BlogBot.Models;
using BlogBot.Utils;

namespace BlogBot.Repository;

public sealed class FileBlogRepository
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _historyPath;

    public FileBlogRepository(string repoRoot, string postsDirectory, string historyPath, string authorProfilePath)
    {
        RepoRoot = repoRoot;
        PostsDirectory = postsDirectory;
        _historyPath = historyPath;
        AuthorProfilePath = authorProfilePath;
    }

    public string RepoRoot { get; }
    public string PostsDirectory { get; }
    public string AuthorProfilePath { get; }

    public TopicHistory LoadHistory() => TopicHistory.Load(_historyPath);

    public PostIndex BuildIndex(TopicHistory history) => PostIndex.Build(RepoRoot, PostsDirectory, history);

    public BlockList LoadBlockList() => BlockList.Load(Path.Combine(RepoRoot, "meta", "blocked_sources.txt"));

    /// <summary>
    /// Returns the author profile without HTML comments (the template's instructions), or null if nothing is filled in.
    /// </summary>
    public string? LoadAuthorProfile()
    {
        if (!File.Exists(AuthorProfilePath)) return null;

        var text = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(AuthorProfilePath), "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        // Headings alone mean the template was never filled in.
        var hasContent = text.Split('\n').Any(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'));
        return hasContent ? text.Trim() : null;
    }

    public string CreatePostFile(Topic topic, GeneratedPost post)
    {
        Directory.CreateDirectory(PostsDirectory);

        var now = DateTime.UtcNow;
        var slug = Slug.From(topic.Title);
        var path = Path.Combine(PostsDirectory, $"{now:yyyy-MM-dd}-{slug}.md");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(PostsDirectory, $"{now:yyyy-MM-dd}-{slug}-{n}.md");

        File.WriteAllText(path, Render(topic, post, now), Utf8NoBom);
        return path;
    }

    public static string Render(Topic topic, GeneratedPost post, DateTime dateUtc)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("layout: post\n");
        sb.Append($"title: {Yaml(topic.Title)}\n");
        sb.Append($"date: {dateUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} +0000\n");
        sb.Append("categories: ai\n");
        sb.Append($"description: {Yaml(post.Description.Trim())}\n");
        // jekyll-feed (0.15 on GitHub Pages) builds the RSS <summary> from the excerpt, which Zapier posts to LinkedIn.
        sb.Append($"excerpt: {Yaml(post.Description.Trim())}\n");

        var tags = TagVocabulary.Normalize(post.Tags);
        if (tags.Count > 0)
            sb.Append($"tags: [{string.Join(", ", tags.Select(Yaml))}]\n");

        // Titles are written in a parallel list so the page can show "Article title" instead of a raw URL,
        // while older tooling that reads plain "sources" URLs keeps working.
        var sources = (topic.Sources.Count > 0
                ? topic.Sources.SelectMany(s => new[]
                {
                    (Title: s.Title, Url: s.Url),
                    (Title: DiscussionTitle(s), Url: s.DiscussionUrl ?? string.Empty)
                })
                : topic.SupportingUrls.Select(u => (Title: string.Empty, Url: u)))
            .Where(s => IsHttpUrl(s.Url))
            .DistinctBy(s => s.Url)
            .ToList();
        if (sources.Count > 0)
        {
            sb.Append("sources:\n");
            foreach (var source in sources)
                sb.Append($"  - {Yaml(source.Url)}\n");
            sb.Append("source_titles:\n");
            foreach (var source in sources)
                sb.Append($"  - {Yaml(source.Title)}\n");
        }

        sb.Append("---\n\n");
        sb.Append(post.Markdown.Trim()).Append('\n');
        return sb.ToString();
    }

    private static string DiscussionTitle(TopicCandidate source) => source.Source switch
    {
        "hackernews" => $"Hacker News discussion: {source.Title}",
        "reddit" => $"Reddit discussion: {source.Title}",
        _ => $"Discussion: {source.Title}"
    };

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string Yaml(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ") + "\"";
}
