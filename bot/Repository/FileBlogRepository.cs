using System.Globalization;
using System.Text;
using BlogBot.Models;

namespace BlogBot.Repository;

public sealed class FileBlogRepository
{
    private readonly string _postsDirectory;
    private readonly string _historyPath;

    public FileBlogRepository(string postsDirectory, string historyPath)
    {
        _postsDirectory = postsDirectory;
        _historyPath = historyPath;
    }

    public TopicHistory LoadHistory()
    {
        return TopicHistory.Load(_historyPath);
    }

    public void SaveHistory(TopicHistory history)
    {
        history.Save(_historyPath);
    }

    public string CreatePostFile(Topic topic, string markdownBody)
    {
        if (!Directory.Exists(_postsDirectory))
        {
            Directory.CreateDirectory(_postsDirectory);
        }

        var now = DateTime.UtcNow;
        var slug = Slugify(topic.Title);
        var fileName = $"{now:yyyy-MM-dd}-{slug}.md";
        var path = Path.Combine(_postsDirectory, fileName);

        var frontMatter = BuildFrontMatter(topic, now);
        var content = frontMatter + Environment.NewLine + markdownBody.Trim() + Environment.NewLine;

        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string BuildFrontMatter(Topic topic, DateTime dateUtc)
    {
        var dateStr = dateUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // Basic Jekyll front matter; adjust categories/tags as you like.
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(@"layout: post");
        sb.AppendLine($@"title: ""{topic.Title.Replace("\"", "\\\"")}""");
        sb.AppendLine($"date: {dateStr} +0000");
        sb.AppendLine("categories: dotnet blog");
        if (!string.IsNullOrWhiteSpace(topic.PrimaryUrl))
        {
            sb.AppendLine($@"canonical_url: ""{topic.PrimaryUrl}""");
        }

        sb.AppendLine("---");
        return sb.ToString();
    }

    private static string Slugify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "post";

        text = text.ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) || c == '-' || c == '_')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "post" : slug;
    }
}
