using BlogBot.Models;

namespace BlogBot.Llm;

public interface ILlmClient
{
    /// <summary>
    /// Proposes post topics grounded in the given candidates, avoiding the excluded (already covered) titles.
    /// </summary>
    Task<List<Topic>> ProposeTopicsAsync(
        IReadOnlyList<TopicCandidate> candidates,
        IReadOnlyList<string> excludedTitles,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the first draft of a post (Markdown body, no front matter).
    /// </summary>
    Task<string> WriteDraftAsync(Topic topic, string sourceMaterial, string? authorProfile, CancellationToken ct = default);

    /// <summary>
    /// Edits a draft: removes AI tells and invented experience, fixes the listed issues, and returns
    /// the final Markdown plus description and tags.
    /// </summary>
    Task<GeneratedPost> EditPostAsync(
        Topic topic,
        string draft,
        string sourceMaterial,
        string? authorProfile,
        IReadOnlyList<string> issues,
        CancellationToken ct = default);

    /// <summary>
    /// Checks each claim strictly against its evidence using the given model. The model may skip items; only the
    /// verdicts it actually gave are returned.
    /// </summary>
    Task<List<ClaimVerdict>> VerifyClaimsAsync(IReadOnlyList<ClaimCheck> claims, string model, CancellationToken ct = default);

    /// <summary>
    /// For each rejected sentence, returns a replacement that matches the evidence, an opinion-framed version, or an
    /// empty string to delete it. Ids the model doesn't answer are absent from the result.
    /// </summary>
    Task<Dictionary<int, string>> RepairSentencesAsync(IReadOnlyList<RepairRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// Writes a 2-3 sentence teaser using only facts from the given verified sentences.
    /// </summary>
    Task<string> RewriteTeaserAsync(IReadOnlyList<string> verifiedSentences, CancellationToken ct = default);

    /// <summary>
    /// Fixes a Mermaid diagram that failed to render. Returns the corrected diagram source.
    /// </summary>
    Task<string> FixDiagramAsync(string diagram, string error, CancellationToken ct = default);
}
