using System.Diagnostics;
using BlogBot.Models;

namespace BlogBot.Repository;

/// <summary>
/// Everything already published or waiting in an open PR: the source of truth for de-duplication.
/// Built from the front matter of _posts (on the current checkout and on open pull request branches)
/// plus meta/topics_history.json.
/// </summary>
public sealed class PostIndex
{
    public sealed record Entry(string Title, IReadOnlyList<string> SourceUrls, string Origin);

    public List<Entry> Entries { get; } = new();

    public IEnumerable<string> Titles => Entries.Select(e => e.Title).Distinct(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SourceUrls =>
        Entries.SelectMany(e => e.SourceUrls).Select(NormalizeUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static PostIndex Build(string repoRoot, string postsDirectory, TopicHistory history)
    {
        var index = new PostIndex();

        if (Directory.Exists(postsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(postsDirectory, "*.md"))
                index.AddFromMarkdown(File.ReadAllText(file), $"_posts/{Path.GetFileName(file)}");
        }

        foreach (var item in history.Items)
        {
            // Old history entries only stored the slug; turn it back into words for similarity checks.
            var title = string.IsNullOrWhiteSpace(item.Title) ? item.TopicId.Replace('-', ' ') : item.Title;
            index.Entries.Add(new Entry(title, item.SourceUrls, "history"));
        }

        index.AddFromRemoteBranches(repoRoot);
        return index;
    }

    /// <summary>
    /// Posts that never reached main still count as used: those in open pull requests (refs/remotes/pending/) and
    /// those in post PRs closed without merging in the last 30 days (refs/remotes/closed/). The workflow fetches
    /// both before running the bot. Closed PRs are durable memory: unlike the Actions cache, they don't expire.
    /// </summary>
    private void AddFromRemoteBranches(string repoRoot)
    {
        var refs = Git(repoRoot, "for-each-ref", "--format=%(refname)", "refs/remotes/pending/", "refs/remotes/closed/");
        if (refs is null) return;

        foreach (var gitRef in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var files = Git(repoRoot, "ls-tree", "-r", "--name-only", gitRef, "--", "_posts");
            if (files is null) continue;

            foreach (var path in files.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(repoRoot, path))) continue; // already indexed from the checkout

                var content = Git(repoRoot, "show", $"{gitRef}:{path}");
                if (content is not null)
                    AddFromMarkdown(content, $"{gitRef}:{path}");
            }
        }
    }

    private void AddFromMarkdown(string content, string origin)
    {
        var frontMatter = FrontMatter.Parse(content);
        if (frontMatter.Title is null) return;

        var urls = frontMatter.Sources.ToList();
        if (frontMatter.CanonicalUrl is not null)
            urls.Add(frontMatter.CanonicalUrl);

        Entries.Add(new Entry(frontMatter.Title, urls, origin));
    }

    public static string NormalizeUrl(string url) =>
        url.Trim().TrimEnd('/').Replace("http://", "https://").Replace("://www.", "://");

    private static string? Git(string workingDirectory, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"git {string.Join(' ', args)} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Minimal Jekyll front matter reader for the fields the bot writes: title, canonical_url and a sources list.
    /// </summary>
    private sealed class FrontMatter
    {
        public string? Title { get; private set; }
        public string? CanonicalUrl { get; private set; }
        public List<string> Sources { get; } = new();

        public static FrontMatter Parse(string content)
        {
            var result = new FrontMatter();
            var lines = content.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count == 0 || lines[0].Trim() != "---") return result;

            var inSources = false;
            for (var i = 1; i < lines.Count && lines[i].Trim() != "---"; i++)
            {
                var line = lines[i];

                if (inSources && line.TrimStart().StartsWith("- "))
                {
                    result.Sources.Add(Unquote(line.TrimStart()[2..]));
                    continue;
                }

                inSources = false;
                var colon = line.IndexOf(':');
                if (colon <= 0 || char.IsWhiteSpace(line[0])) continue;

                var key = line[..colon].Trim();
                var value = Unquote(line[(colon + 1)..]);

                switch (key)
                {
                    case "title": result.Title = value; break;
                    case "canonical_url": result.CanonicalUrl = value; break;
                    case "sources": inSources = value.Length == 0; break;
                }
            }

            return result;
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1].Replace("\\\"", "\"");
            return value;
        }
    }
}
