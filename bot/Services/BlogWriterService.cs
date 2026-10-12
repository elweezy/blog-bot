using System.Text;
using BlogBot.Llm;
using BlogBot.Models;
using BlogBot.Repository;

namespace BlogBot.Services;

public sealed class BlogWriterService
{
    private const int MaxSourceCharsPerItem = 6000;
    private const int MaxExcludedTitlesInPrompt = 80;
    private const int MaxEditAttempts = 2;

    private readonly TopicDiscoveryService _discovery;
    private readonly ILlmClient _llm;
    private readonly FileBlogRepository _blogRepo;
    private readonly MermaidValidator _mermaid;
    private readonly bool _dryRun = Environment.GetEnvironmentVariable("DRY_RUN") == "1";
    private readonly bool _requireMermaidValidation = Environment.GetEnvironmentVariable("REQUIRE_MERMAID_VALIDATION") == "1";

    public BlogWriterService(
        TopicDiscoveryService discovery,
        ILlmClient llm,
        FileBlogRepository blogRepo,
        MermaidValidator mermaid)
    {
        _discovery = discovery;
        _llm = llm;
        _blogRepo = blogRepo;
        _mermaid = mermaid;
    }

    /// <returns>The created post, or null when nothing new was worth writing.</returns>
    public async Task<(Topic Topic, GeneratedPost Post, string? Path)?> RunAsync(CancellationToken ct = default)
    {
        // Fail before spending any free-tier quota if diagrams can't be validated.
        if (_requireMermaidValidation && !_mermaid.IsAvailable)
            throw new InvalidOperationException("REQUIRE_MERMAID_VALIDATION=1 but mermaid-cli (mmdc) was not found.");

        // The history file is read-only now: legacy entries still count, but new posts are tracked through their
        // own front matter (on main and on unmerged auto/* branches). Writing it from every PR caused merge conflicts.
        var index = _blogRepo.BuildIndex(_blogRepo.LoadHistory());
        Console.WriteLine($"Indexed {index.Entries.Count} existing posts/history entries.");

        var candidates = await _discovery.GetCandidatesAsync(ct);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No topic candidates found. Exiting.");
            return null;
        }

        var chosen = await ChooseTopicAsync(candidates, index, ct);
        if (chosen is null)
        {
            Console.WriteLine("Every proposed topic duplicates an existing post. Skipping this run instead of repeating.");
            return null;
        }

        Console.WriteLine($"Chosen topic: {chosen.Title}");
        Console.WriteLine($"Angle:        {chosen.Angle}");

        var sourceMaterial = await BuildSourceMaterialAsync(chosen, ct);
        var authorProfile = _blogRepo.LoadAuthorProfile();
        var sanitizer = new PostSanitizer(ExtractSection(authorProfile, "Real experiences"));

        var post = await WriteAndEditAsync(chosen, sourceMaterial, authorProfile, sanitizer, ct);
        post.Markdown = await ValidateDiagramsAsync(post.Markdown, ct);

        // Removing an unfixable diagram can leave the post without its required structure.
        var finalIssues = sanitizer.FindIssues(post.Markdown).Where(i => i.Blocking).ToList();
        if (finalIssues.Count > 0)
            throw new InvalidOperationException(
                "Post lost required structure after diagram validation, not publishing it:\n" +
                string.Join("\n", finalIssues.Select(i => "- " + i.Message)));

        if (_dryRun)
        {
            Console.WriteLine("DRY_RUN=1, not writing files. Generated post:\n");
            Console.WriteLine(FileBlogRepository.Render(chosen, post, DateTime.UtcNow));
            return (chosen, post, null);
        }

        var postPath = _blogRepo.CreatePostFile(chosen, post);
        Console.WriteLine($"Created post: {postPath}");

        return (chosen, post, postPath);
    }

    private async Task<Topic?> ChooseTopicAsync(List<TopicCandidate> candidates, PostIndex index, CancellationToken ct)
    {
        var existingTitles = index.Titles.ToList();
        var usedUrls = index.SourceUrls;
        var excluded = existingTitles.TakeLast(MaxExcludedTitlesInPrompt).ToList();

        // Second round: also exclude what the model proposed and we rejected, so it looks elsewhere.
        for (var round = 1; round <= 2; round++)
        {
            var topics = await _llm.ProposeTopicsAsync(candidates, excluded, ct);
            Console.WriteLine($"Round {round}: model proposed {topics.Count} topics.");

            var fresh = new List<Topic>();
            foreach (var topic in topics)
            {
                var reason = DuplicateReason(topic, existingTitles, usedUrls);
                if (reason is null)
                {
                    fresh.Add(topic);
                    continue;
                }

                Console.WriteLine($"  rejected: {topic.Title} ({reason})");
                excluded.Add(topic.Title);
            }

            if (fresh.Count == 0) continue;

            // Rank by how much fresh discussion backs the topic, instead of picking at random.
            foreach (var topic in fresh)
                topic.AggregateScore = topic.Sources.Count == 0 ? 0 : topic.Sources.Sum(TopicDiscoveryService.Score);

            return fresh.OrderByDescending(t => t.AggregateScore).First();
        }

        return null;
    }

    private static string? DuplicateReason(Topic topic, List<string> existingTitles, HashSet<string> usedUrls)
    {
        if (topic.Sources.Count == 0)
            return "not grounded in any source";

        var best = existingTitles
            .Select(t => (Title: t, Score: TopicSimilarity.Score(topic.Title, t)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (best.Title is not null && best.Score >= TopicSimilarity.DuplicateThreshold)
            return $"similar to \"{best.Title}\", score {best.Score:0.00}";

        var primary = PostIndex.NormalizeUrl(topic.PrimaryUrl);
        if (primary.Length > 0 && usedUrls.Contains(primary))
            return $"primary source already used: {topic.PrimaryUrl}";

        return null;
    }

    private async Task<string> BuildSourceMaterialAsync(Topic topic, CancellationToken ct)
    {
        var sources = topic.Sources.Take(3).ToList();
        var texts = await Task.WhenAll(sources.Select(s => _discovery.FetchSourceTextAsync(s, MaxSourceCharsPerItem, ct)));

        var sb = new StringBuilder();
        for (var i = 0; i < sources.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] {sources[i].Source}: {sources[i].Title}");
            sb.AppendLine(texts[i]);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<GeneratedPost> WriteAndEditAsync(
        Topic topic,
        string sourceMaterial,
        string? authorProfile,
        PostSanitizer sanitizer,
        CancellationToken ct)
    {
        var draft = sanitizer.Clean(await _llm.WriteDraftAsync(topic, sourceMaterial, authorProfile, ct));
        var issues = sanitizer.FindIssues(draft);
        LogIssues("Draft", issues);

        var current = draft;
        for (var attempt = 1; attempt <= MaxEditAttempts; attempt++)
        {
            var post = await _llm.EditPostAsync(topic, current, sourceMaterial, authorProfile,
                issues.Select(i => i.Message).ToList(), ct);
            post.Markdown = sanitizer.Clean(post.Markdown);

            issues = sanitizer.FindIssues(post.Markdown);
            LogIssues($"Edit {attempt}", issues);

            if (!issues.Any(i => i.Blocking))
                return post;

            // Keep editing the edited text only if it still looks like a full post; otherwise go back to the draft.
            var lostStructure = issues.Any(i => i.Message.StartsWith("No ```mermaid") || i.Message.StartsWith("Missing the"));
            current = lostStructure || post.Markdown.Length < draft.Length / 2 ? draft : post.Markdown;
        }

        throw new InvalidOperationException(
            "Post still has blocking problems after editing, not publishing it:\n" +
            string.Join("\n", issues.Where(i => i.Blocking).Select(i => "- " + i.Message)));
    }

    /// <summary>
    /// Renders every diagram; a broken one gets one repair attempt, and is removed (with its caption) if still broken.
    /// </summary>
    private async Task<string> ValidateDiagramsAsync(string markdown, CancellationToken ct)
    {
        if (!_mermaid.IsAvailable)
        {
            var message = "mermaid-cli (mmdc) not found, so diagrams were not validated.";
            if (_requireMermaidValidation) throw new InvalidOperationException(message);
            Console.Error.WriteLine("Warning: " + message);
            return markdown;
        }

        foreach (var block in PostSanitizer.MermaidBlocks(markdown).AsEnumerable().Reverse())
        {
            var diagram = block.Groups[1].Value;
            var error = await _mermaid.ValidateAsync(diagram, ct);
            if (error is null) continue;

            Console.WriteLine($"Diagram failed to render, asking for a fix:\n{error}");
            var repaired = await _llm.FixDiagramAsync(diagram, error, ct);
            if (await _mermaid.ValidateAsync(repaired, ct) is null)
            {
                markdown = markdown.Remove(block.Index, block.Length)
                    .Insert(block.Index, $"```mermaid\n{repaired}\n```");
                continue;
            }

            Console.WriteLine("Repair failed; removing the diagram and its caption.");
            var end = block.Index + block.Length;
            var rest = markdown[end..];
            var caption = System.Text.RegularExpressions.Regex.Match(rest, @"^\s*\n\*Figure:[^\n]*\*[ \t]*(?=\n|$)");
            if (caption.Success) end += caption.Length;
            markdown = markdown.Remove(block.Index, end - block.Index);
        }

        return System.Text.RegularExpressions.Regex.Replace(markdown, @"\n{3,}", "\n\n");
    }

    private static void LogIssues(string stage, List<PostSanitizer.Issue> issues)
    {
        Console.WriteLine($"{stage}: {issues.Count(i => i.Blocking)} blocking, {issues.Count(i => !i.Blocking)} minor issues.");
        foreach (var issue in issues)
            Console.WriteLine($"  {(issue.Blocking ? "BLOCKING" : "minor")}: {issue.Message}");
    }

    private static string? ExtractSection(string? markdown, string heading)
    {
        if (markdown is null) return null;

        var lines = markdown.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart('#', ' ').StartsWith(heading, StringComparison.OrdinalIgnoreCase) && l.StartsWith('#'));
        if (start < 0) return null;

        var section = lines.Skip(start + 1).TakeWhile(l => !l.StartsWith('#'));
        return string.Join('\n', section).Trim();
    }
}
