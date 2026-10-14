using System.Text;
using System.Text.RegularExpressions;
using BlogBot.Llm;
using BlogBot.Models;
using BlogBot.Repository;

namespace BlogBot.Services;

public sealed class BlogWriterService
{
    private const int MaxSourceCharsPerItem = 15000;
    private const int MaxExcludedTitlesInPrompt = 80;
    private const int MaxEditAttempts = 3;

    // Below this much fetched text the writer would be working from the title alone, so the topic is skipped.
    private const int MinSourceChars = 1500;

    // Publishing without review: a post needs enough verified claims, and if the fact-checkers reject more than
    // this share of them, the draft is too unreliable to publish even after pruning.
    private const int MinCitedClaims = 6;
    private const double MaxRejectedShare = 0.25;

    // Uncited sentences are deleted when they state facts; if this share of all prose goes, the draft isn't trusted.
    private const double MaxRemovedShare = 0.4;

    private const int TeaserId = -1;
    private const int DiagramIdBase = 10_000;
    private const int UncitedIdBase = 20_000;

    /// <summary>One-line result of the fact-check for the PR description, e.g. "11/12 cited sentences verified ...".</summary>
    public string VerificationSummary { get; private set; } = string.Empty;

    private sealed record SourceSet(
        string Material,
        IReadOnlyDictionary<int, string> Texts,
        IReadOnlyDictionary<int, (string Title, string Url)> Links);

    private readonly TopicDiscoveryService _discovery;
    private readonly ILlmClient _llm;
    private readonly FileBlogRepository _blogRepo;
    private readonly MermaidValidator _mermaid;
    private readonly bool _dryRun = Environment.GetEnvironmentVariable("DRY_RUN") == "1";
    private readonly bool _requireMermaidValidation = Environment.GetEnvironmentVariable("REQUIRE_MERMAID_VALIDATION") == "1";

    // Two independent fact-checkers; a claim survives only if both accept it. Each checker has a fallback list
    // (checkers separated by ';', fallbacks by ','), and the two never use the same model.
    private readonly string[][] _verifierSlots =
        (Environment.GetEnvironmentVariable("GEMINI_VERIFIER_MODELS") is { Length: > 0 } v
            ? v
            : "gemini-3.8-flash,gemini-3.6-flash;gemini-2.5-flash,gemini-3.6-flash")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(slot => slot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();

    // The models that actually did the fact-check in this run, for the summary.
    private readonly List<string> _usedVerifiers = new();

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

        var ledger = _blogRepo.LoadLedger();
        var candidates = await _discovery.GetCandidatesAsync(ct);

        // Sources on cooldown or on the block list never reach the topic model, so it can't keep proposing them.
        var available = candidates.Where(c => ledger.Unavailable(c.Url) is null && ledger.Unavailable(c.DiscussionUrl ?? "") is null).ToList();
        foreach (var skipped in candidates.Except(available).Take(10))
            Console.WriteLine($"  source skipped: {Truncate(skipped.Title, 80)} ({ledger.Unavailable(skipped.Url) ?? ledger.Unavailable(skipped.DiscussionUrl ?? "")})");
        candidates = available;

        if (candidates.Count == 0)
        {
            Console.WriteLine("No topic candidates found. Exiting.");
            return null;
        }

        var ranked = await RankTopicsAsync(candidates, index, ct);
        if (ranked.Count == 0)
        {
            Console.WriteLine("Every proposed topic duplicates an existing post. Skipping this run instead of repeating.");
            return null;
        }

        // Take the best topic whose sources actually have enough text to write from.
        Topic? chosen = null;
        SourceSet? sources = null;
        foreach (var topic in ranked)
        {
            sources = await BuildSourcesAsync(topic, ct);
            var textLength = sources.Texts.Values.Sum(t => t.Length);
            if (textLength >= MinSourceChars)
            {
                chosen = topic;
                break;
            }

            Console.WriteLine($"  skipped: {topic.Title} (only {textLength} characters of source text)");
        }

        if (chosen is null || sources is null)
        {
            Console.WriteLine("No new topic has enough source material. Skipping this run.");
            return null;
        }

        // Record before writing, so the source cools down even if this run ends without a post.
        ledger.Record(chosen.Sources.Take(3).SelectMany(s => new[] { s.Url, s.DiscussionUrl ?? "" }), chosen.Title);

        Console.WriteLine($"Chosen topic: {chosen.Title}");
        Console.WriteLine($"Angle:        {chosen.Angle}");
        Console.WriteLine($"Source text:  {sources.Material.Length} characters");
        var authorProfile = _blogRepo.LoadAuthorProfile();
        var sanitizer = new PostSanitizer(ExtractSection(authorProfile, "Real experiences"));

        var post = await WriteAndEditAsync(chosen, sources, authorProfile, sanitizer, ct);
        post.Markdown = await ValidateDiagramsAsync(post.Markdown, ct);

        // Posts are published without human review: every claim is checked against its quote by two models.
        var verified = await VerifyAndPruneAsync(post, sources, ct);
        if (verified is null)
        {
            Console.WriteLine("The fact-check rejected too much of this draft. Not publishing anything this run.");
            return null;
        }

        // Pruning can remove a diagram or section; the post must still be complete.
        var finalIssues = CheckPost(verified, sources, sanitizer).Where(i => i.Blocking).ToList();
        if (finalIssues.Count > 0)
        {
            Console.WriteLine("Post is incomplete after fact-check pruning. Not publishing anything this run:");
            foreach (var issue in finalIssues) Console.WriteLine("  - " + issue.Message);
            return null;
        }

        post = verified;
        // Citations were the fact-check's evidence; readers get prose attribution and the sources list instead.
        post.Markdown = Citations.StripMarkers(post.Markdown);

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

    /// <returns>New (non-duplicate) topics, best first; empty when every proposal duplicates an existing post.</returns>
    private async Task<List<Topic>> RankTopicsAsync(List<TopicCandidate> candidates, PostIndex index, CancellationToken ct)
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

            return fresh.OrderByDescending(t => t.AggregateScore).ToList();
        }

        return [];
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

    private async Task<SourceSet> BuildSourcesAsync(Topic topic, CancellationToken ct)
    {
        var picked = topic.Sources.Take(3).ToList();
        var texts = await Task.WhenAll(picked.Select(s => _discovery.FetchSourceTextAsync(s, MaxSourceCharsPerItem, ct)));

        var sb = new StringBuilder();
        var byLabel = new Dictionary<int, string>();
        var links = new Dictionary<int, (string Title, string Url)>();
        for (var i = 0; i < picked.Count; i++)
        {
            var label = i + 1;
            byLabel[label] = texts[i];
            links[label] = (picked[i].Title, picked[i].Url);
            sb.AppendLine($"[S{label}] {picked[i].Source}: {picked[i].Title}");
            sb.AppendLine(texts[i]);
            sb.AppendLine();
        }

        return new SourceSet(sb.ToString(), byLabel, links);
    }

    /// <summary>
    /// Everything the deterministic checks can find: invented experience, structure, style, unsupported figures in
    /// the body and teaser, quotes that don't exist in their source, and too few citations.
    /// </summary>
    private static List<PostSanitizer.Issue> CheckPost(GeneratedPost post, SourceSet sources, PostSanitizer sanitizer)
    {
        var issues = sanitizer.FindIssues(post.Markdown, sources.Material);
        issues.AddRange(PostSanitizer.FindTeaserIssues(post.Description, sources.Material));
        issues.AddRange(CitationIssues(post.Markdown, sources));
        return issues;
    }

    private static IEnumerable<PostSanitizer.Issue> CitationIssues(string markdown, SourceSet sources)
    {
        foreach (var quote in Citations.InvalidQuotes(markdown, sources.Texts))
            yield return new PostSanitizer.Issue(
                $"The citation {quote} quotes words that are not in that source. Copy the exact words from the " +
                "source, cite the right source label, or delete the sentence.",
                Blocking: true);

        var claims = Citations.ExtractClaims(markdown).Count;
        if (claims < MinCitedClaims)
            yield return new PostSanitizer.Issue(
                $"Only {claims} sentences carry a citation marker; at least {MinCitedClaims} are needed. Every " +
                "sentence that states a fact from the sources must end with [[S<n>: \"exact quote\"]].",
                Blocking: true);
    }

    /// <summary>
    /// Sends every sentence outside the Draft design (cited ones against their quotes, uncited ones to confirm they
    /// are opinion or reasoning), every diagram outside the Draft design, and the teaser to each verifier model.
    /// Anything a verifier rejects is removed. Returns null when too much was rejected to trust the rest.
    /// </summary>
    private async Task<GeneratedPost?> VerifyAndPruneAsync(GeneratedPost post, SourceSet sources, CancellationToken ct)
    {
        var markdown = post.Markdown;
        var draftDesignAt = Regex.Match(markdown, @"^#{2,3}\s+.*draft design", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var beforeDesign = draftDesignAt.Success ? markdown[..draftDesignAt.Index] : markdown; // the Draft design is the writer's own proposal

        var claims = Citations.ExtractClaims(markdown);
        var uncited = Citations.ExtractUncited(beforeDesign, UncitedIdBase);
        var allQuotes = claims.SelectMany(c => c.Quotes).ToList();
        var allEvidence = Evidence(allQuotes, sources);

        // Rows of the writer's own Draft design table are design judgments, checked only for facts and figures.
        var designText = draftDesignAt.Success ? Citations.StripMarkers(markdown[draftDesignAt.Index..]) : string.Empty;
        string KindOf(Citations.Claim c) => c.Text.StartsWith('|') && designText.Contains(c.Text) ? "design" : "sentence";

        var checks = claims.Select(c => new ClaimCheck(c.Id, KindOf(c), c.Text, Evidence(c.Quotes, sources))).ToList();
        checks.AddRange(uncited.Select(c => new ClaimCheck(c.Id, "uncited", c.Text, allEvidence)));

        var diagrams = PostSanitizer.MermaidBlocks(markdown)
            .Where(b => !draftDesignAt.Success || b.Index < draftDesignAt.Index)
            .ToList();
        for (var i = 0; i < diagrams.Count; i++)
        {
            var caption = CaptionAfter(markdown, diagrams[i]);
            var sectionQuotes = Citations.QuotesIn(SectionAround(markdown, diagrams[i].Index));
            checks.Add(new ClaimCheck(DiagramIdBase + i, "diagram",
                $"{diagrams[i].Groups[1].Value}\nCaption: {Citations.StripMarkers(caption)}",
                Evidence(Citations.QuotesIn(caption).Concat(sectionQuotes).ToList(), sources)));
        }

        checks.Add(new ClaimCheck(TeaserId, "teaser", post.Description, allEvidence));

        var rejected = await RejectedByAnyVerifierAsync(checks, ct);

        var keptDiagrams = diagrams.Where((_, i) => !rejected.ContainsKey(DiagramIdBase + i)).Count();
        foreach (var (id, reason) in rejected)
        {
            var what = id == TeaserId ? "teaser"
                : id >= UncitedIdBase ? uncited.First(c => c.Id == id).Text
                : id >= DiagramIdBase ? $"diagram {id - DiagramIdBase + 1}"
                : claims[id].Text;
            Console.WriteLine($"  rejected: {Truncate(what, 160)}\n    because {Truncate(reason, 200)}");
        }

        // One repair round: rejected sentences go back to the editor with the reason and evidence; whatever it
        // returns is fact-checked again by both models. Anything that still fails is deleted.
        var proseById = claims.Concat(uncited).ToDictionary(c => c.Id);
        var repairable = proseById.Values.Where(c => rejected.ContainsKey(c.Id)).ToList();
        var outcome = await RepairAsync(repairable, rejected, checks, allEvidence, sources, markdown, ct);

        var deletedCited = claims.Count(c => outcome.TryGetValue(c.Id, out var r) && r.Length == 0);
        var deletedUncited = uncited.Count(c => outcome.TryGetValue(c.Id, out var r) && r.Length == 0);
        var repaired = outcome.Count(o => o.Value.Length > 0);

        var totalProse = claims.Count + uncited.Count;
        if (claims.Count < MinCitedClaims || deletedCited > claims.Count * MaxRejectedShare ||
            deletedCited + deletedUncited > totalProse * MaxRemovedShare)
        {
            Console.WriteLine($"Fact-check: after repair, {deletedCited}/{claims.Count} cited and " +
                              $"{deletedUncited}/{uncited.Count} uncited sentences still fail; too much to publish.");
            return null;
        }

        // Remove rejected diagrams back to front so earlier positions stay valid, then rejected sentences.
        for (var i = diagrams.Count - 1; i >= 0; i--)
            if (rejected.ContainsKey(DiagramIdBase + i))
                markdown = RemoveDiagram(markdown, diagrams[i]);
        markdown = Citations.ReplaceClaims(markdown, outcome.ToDictionary(o => proseById[o.Key].Text, o => o.Value));

        var description = post.Description;
        var teaserNote = "verified";
        if (rejected.ContainsKey(TeaserId))
        {
            (description, teaserNote) = await ReplaceTeaserAsync(markdown, allEvidence, ct);
        }

        VerificationSummary =
            $"{claims.Count - deletedCited}/{claims.Count} cited and {uncited.Count - deletedUncited}/{uncited.Count} " +
            $"uncited sentences kept ({repaired} repaired and re-checked) by {string.Join(" and ", _usedVerifiers)}, " +
            $"{keptDiagrams}/{diagrams.Count} diagrams kept, teaser {teaserNote}";
        Console.WriteLine($"Fact-check: {VerificationSummary}.");

        return new GeneratedPost { Markdown = markdown, Description = description, Tags = post.Tags };
    }

    /// <summary>
    /// Runs every check past each fact-checker slot. A slot falls back to its next model when one is unavailable,
    /// but never to a model another slot already used, so the two verdicts stay independent. Items a model skips
    /// are asked again once; anything still unanswered counts as rejected. Throws when a slot has no usable model,
    /// so nothing is published without two independent checks.
    /// </summary>
    /// <summary>
    /// Asks the editor to fix rejected sentences, validates their quotes, and fact-checks the replacements again.
    /// Returns, per rejected sentence id, the verified replacement or an empty string meaning "delete".
    /// </summary>
    private async Task<Dictionary<int, string>> RepairAsync(
        List<Citations.Claim> rejectedProse,
        Dictionary<int, string> reasons,
        List<ClaimCheck> originalChecks,
        List<string> allEvidence,
        SourceSet sources,
        string markdown,
        CancellationToken ct)
    {
        var outcome = rejectedProse.ToDictionary(c => c.Id, _ => string.Empty);
        var paragraphs = Citations.StripMarkers(markdown).Split("\n\n");
        if (rejectedProse.Count == 0) return outcome;

        var kinds = originalChecks.ToDictionary(c => c.Id, c => c.Kind);
        var requests = rejectedProse.Select(c => new RepairRequest(
            c.Id, kinds[c.Id], c.Text, reasons[c.Id],
            c.Quotes.Count > 0 ? Evidence(c.Quotes, sources).Concat(allEvidence).Distinct().Take(40).ToList() : allEvidence,
            paragraphs.FirstOrDefault(p => p.Contains(c.Text.TrimStart('-', ' '))) ?? string.Empty)).ToList();

        Dictionary<int, string> replacements;
        try
        {
            replacements = await _llm.RepairSentencesAsync(requests, ct);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"  repair unavailable ({Truncate(ex.Message.Split('\n')[0], 120)}); deleting rejected sentences.");
            return outcome;
        }

        // A replacement must keep its quotes real; otherwise it is dropped before the re-check.
        var rechecks = new List<ClaimCheck>();
        foreach (var request in requests)
        {
            if (!replacements.TryGetValue(request.Id, out var replacement) || string.IsNullOrWhiteSpace(replacement))
                continue;

            replacement = Citations.FixPlacement(replacement.Trim());
            if (PostSanitizer.IsIncomplete(Citations.StripMarkers(replacement), requireFinalPunctuation: true))
            {
                Console.WriteLine($"  repair dropped (incomplete sentence): {Truncate(Citations.StripMarkers(replacement), 140)}");
                continue;
            }

            if (Citations.InvalidQuotes(replacement, sources.Texts).Count > 0)
            {
                Console.WriteLine($"  repair dropped (its quote is not in the source): {Truncate(Citations.StripMarkers(replacement), 140)}");
                continue;
            }

            var quotes = Citations.QuotesIn(replacement);
            var kind = request.Kind == "design" ? "design" : quotes.Count > 0 ? "sentence" : "uncited";
            replacements[request.Id] = replacement;
            rechecks.Add(new ClaimCheck(request.Id, kind, Citations.StripMarkers(replacement).Trim(),
                quotes.Count > 0 ? Evidence(quotes, sources) : allEvidence));
        }

        if (rechecks.Count == 0) return outcome;

        var stillRejected = await RejectedByAnyVerifierAsync(rechecks, ct);
        foreach (var check in rechecks)
        {
            if (stillRejected.TryGetValue(check.Id, out var why))
            {
                Console.WriteLine($"  repair failed: {Truncate(check.Text, 140)}\n    because {Truncate(why, 160)}");
                continue;
            }

            outcome[check.Id] = replacements[check.Id];
            Console.WriteLine($"  repaired: {Truncate(check.Text, 160)}");
        }

        return outcome;
    }

    private async Task<Dictionary<int, string>> RejectedByAnyVerifierAsync(List<ClaimCheck> checks, CancellationToken ct)
    {
        var rejected = new Dictionary<int, string>();
        var usedThisRound = new List<string>();

        foreach (var slot in _verifierSlots)
        {
            List<ClaimVerdict>? verdicts = null;
            foreach (var model in slot.Where(m => !usedThisRound.Contains(m)))
            {
                try
                {
                    verdicts = await _llm.VerifyClaimsAsync(checks, model, ct);

                    var missing = checks.Where(c => verdicts.All(v => v.Id != c.Id)).ToList();
                    if (missing.Count > 0)
                    {
                        Console.WriteLine($"  {model} skipped {missing.Count} item(s); asking again for those.");
                        verdicts.AddRange(await _llm.VerifyClaimsAsync(missing, model, ct));
                    }

                    foreach (var unanswered in checks.Where(c => verdicts.All(v => v.Id != c.Id)))
                        rejected.TryAdd(unanswered.Id, $"{model}: no verdict given, treated as unverified");

                    usedThisRound.Add(model);
                    break;
                }
                catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
                {
                    Console.WriteLine($"  fact-checker {model} unavailable ({Truncate(ex.Message.Split('\n')[0], 120)}); trying the next one.");
                    verdicts = null;
                }
            }

            if (verdicts is null)
                throw new InvalidOperationException(
                    $"No usable fact-checker for slot [{string.Join(", ", slot)}] distinct from [{string.Join(", ", usedThisRound)}]; " +
                    "not publishing a post that was checked fewer than twice.");

            foreach (var verdict in verdicts.Where(v => !v.Supported))
                rejected.TryAdd(verdict.Id, $"{usedThisRound[^1]}: {verdict.Reason}");
        }

        _usedVerifiers.Clear();
        _usedVerifiers.AddRange(usedThisRound);
        return rejected;
    }

    /// <summary>
    /// The teaser goes to LinkedIn as-is, so a rejected one is rewritten from verified sentences and checked again.
    /// If that fails too, it falls back to whole verified sentences, never a cut-off one.
    /// </summary>
    private async Task<(string Teaser, string Note)> ReplaceTeaserAsync(string markdown, List<string> evidence, CancellationToken ct)
    {
        // Prose sentences and list items only: captions and table rows don't read as a summary.
        var verified = Citations.ExtractClaims(markdown)
            .Select(c => c.Text)
            .Where(t => !t.StartsWith('*') && !t.StartsWith('|'))
            .Select(t => t.TrimStart('>', '-', ' '))
            .ToList();

        var rewritten = await _llm.RewriteTeaserAsync(verified, ct);
        if (rewritten.Length is > 0 and <= 320)
        {
            var recheck = await RejectedByAnyVerifierAsync([new ClaimCheck(TeaserId, "teaser", rewritten, evidence)], ct);
            if (recheck.Count == 0)
                return (rewritten, "rewritten from verified sentences and re-checked");

            Console.WriteLine($"  rewritten teaser rejected too: {Truncate(recheck[TeaserId], 200)}");
        }

        // The page shows the teaser above the post, so avoid repeating the opening paragraph when there's an alternative.
        var opening = Citations.StripMarkers(markdown.Split("\n\n").FirstOrDefault(p => !p.TrimStart().StartsWith('#')) ?? string.Empty);
        var beyondOpening = verified.Where(v => !opening.Contains(v)).ToList();
        return (FallbackTeaser(beyondOpening.Count >= 2 ? beyondOpening : verified), "built from whole verified sentences");
    }

    // Whole sentences only, as many as fit in 300 characters (at least one).
    private static string FallbackTeaser(IReadOnlyList<string> verifiedSentences)
    {
        var teaser = new StringBuilder();
        foreach (var sentence in verifiedSentences)
        {
            if (teaser.Length > 0 && teaser.Length + 1 + sentence.Length > 300) break;
            if (teaser.Length > 0) teaser.Append(' ');
            teaser.Append(sentence);
        }

        return teaser.ToString();
    }

    /// <summary>
    /// For each quote, the surrounding passage in its source, so the verifier sees the context a figure belongs to.
    /// </summary>
    private static List<string> Evidence(IEnumerable<Citations.Quote> quotes, SourceSet sources)
    {
        var evidence = new List<string>();
        foreach (var quote in quotes.DistinctBy(q => (q.Source, q.Text)).Take(40))
        {
            if (!sources.Texts.TryGetValue(quote.Source, out var text)) continue;

            var flat = Regex.Replace(text, @"\s+", " ");
            var at = flat.IndexOf(Regex.Replace(quote.Text, @"\s+", " "), StringComparison.OrdinalIgnoreCase);
            var passage = at < 0
                ? quote.Text
                : flat[Math.Max(0, at - 300)..Math.Min(flat.Length, at + quote.Text.Length + 300)];
            evidence.Add($"S{quote.Source} ({sources.Links[quote.Source].Title}): ...{passage}...");
        }

        return evidence;
    }

    private static string CaptionAfter(string markdown, Match block)
    {
        var caption = Regex.Match(markdown[(block.Index + block.Length)..], @"^\s*\n(\*Figure[^\n]*\*)[ \t]*(?=\n|$)");
        return caption.Success ? caption.Groups[1].Value : string.Empty;
    }

    private static string SectionAround(string markdown, int index)
    {
        var start = markdown.LastIndexOf("\n## ", Math.Min(index, markdown.Length - 1), StringComparison.Ordinal);
        var end = markdown.IndexOf("\n## ", index, StringComparison.Ordinal);
        return markdown[(start < 0 ? 0 : start)..(end < 0 ? markdown.Length : end)];
    }

    private static string RemoveDiagram(string markdown, Match block)
    {
        var end = block.Index + block.Length;
        var caption = Regex.Match(markdown[end..], @"^\s*\n\*Figure[^\n]*\*[ \t]*(?=\n|$)");
        if (caption.Success) end += caption.Length;
        return markdown.Remove(block.Index, end - block.Index);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private async Task<GeneratedPost> WriteAndEditAsync(
        Topic topic,
        SourceSet sources,
        string? authorProfile,
        PostSanitizer sanitizer,
        CancellationToken ct)
    {
        var sourceMaterial = sources.Material;
        var draft = sanitizer.Clean(await _llm.WriteDraftAsync(topic, sourceMaterial, authorProfile, ct));
        var issues = sanitizer.FindIssues(draft, sourceMaterial);
        issues.AddRange(CitationIssues(draft, sources));
        LogIssues("Draft", issues);

        var current = draft;
        GeneratedPost? clean = null; // first edit with no blocking issues, kept in case a polish pass makes things worse
        for (var attempt = 1; attempt <= MaxEditAttempts; attempt++)
        {
            var post = await _llm.EditPostAsync(topic, current, sourceMaterial, authorProfile,
                issues.Select(i => i.Message).ToList(), ct);
            post.Markdown = sanitizer.Clean(post.Markdown);

            issues = CheckPost(post, sources, sanitizer);
            LogIssues($"Edit {attempt}", issues);

            if (!issues.Any(i => i.Blocking))
            {
                // Minor issues (a banned word, missing opinions) get one more polish pass while attempts remain.
                if (issues.Count == 0 || clean is not null || attempt == MaxEditAttempts)
                    return post;

                clean = post;
                current = post.Markdown;
                continue;
            }

            if (clean is not null)
                return clean;

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
            markdown = RemoveDiagram(markdown, block);
        }

        return Regex.Replace(markdown, @"\n{3,}", "\n\n");
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
