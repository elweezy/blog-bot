using BlogBot.Models;
using BlogBot.Utils;

namespace BlogBot.Llm;

/// <summary>
/// Offline stand-in for local testing (USE_MOCK_LLM=1). Produces deterministic, structurally valid output.
/// </summary>
public sealed class MockLlmClient : ILlmClient
{
    public Task<List<Topic>> ProposeTopicsAsync(
        IReadOnlyList<TopicCandidate> candidates,
        IReadOnlyList<string> excludedTitles,
        CancellationToken ct = default)
    {
        var topics = candidates
            .Take(8)
            .Select(c => new Topic
            {
                Id = Slug.From(c.Title),
                Title = c.Title,
                Description = $"Mock summary for '{c.Title}'.",
                Angle = "Mock angle.",
                PrimaryUrl = c.Url,
                SupportingUrls = [c.Url],
                Sources = [c],
                AggregateScore = c.Popularity
            })
            .ToList();

        return Task.FromResult(topics);
    }

    public Task<string> WriteDraftAsync(Topic topic, string sourceMaterial, string? authorProfile, CancellationToken ct = default) =>
        Task.FromResult($$"""
            {{topic.Description}}

            ## How the request flows

            ```mermaid
            flowchart LR
              U[Client] -->|question| R[Retriever]
              R -->|top 20 chunks| K[Reranker]
              K -->|top 5| L[LLM]
              L -->|answer| U
            ```
            *Figure: Mock diagram for {{topic.Title}}.*

            ## Draft design

            Assumptions: 50 requests per second, 2 second latency budget.

            | Option | When it wins | Cost / risk |
            |---|---|---|
            | A | Small corpus | Recall drops as data grows |
            | B | Large corpus | Extra hop adds latency |

            Open questions:

            - How fresh does the index need to be?
            """);

    public Task<GeneratedPost> EditPostAsync(
        Topic topic,
        string draft,
        string sourceMaterial,
        string? authorProfile,
        IReadOnlyList<string> issues,
        CancellationToken ct = default) =>
        Task.FromResult(new GeneratedPost
        {
            Markdown = draft,
            Description = $"Mock description for {topic.Title}.",
            Tags = ["mock", "ai"]
        });

    public Task<string> FixDiagramAsync(string diagram, string error, CancellationToken ct = default) =>
        Task.FromResult(diagram);
}
