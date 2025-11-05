using BlogBot.Models;

namespace BlogBot.OpenAI;

public interface IOpenAIClient
{
    /// <summary>
    /// Given a list of real topic candidates, returns exactly 10 topics.
    /// In production, this would call GPT-5 with a JSON response_format.
    /// </summary>
    Task<List<Topic>> ClusterTopicsAsync(List<TopicCandidate> candidates, CancellationToken ct = default);

    /// <summary>
    /// Given a topic, returns a full Markdown article body (no front matter).
    /// In production, this would call GPT-5 to generate the blog content.
    /// </summary>
    Task<string> GenerateBlogPostAsync(Topic topic, CancellationToken ct = default);
}
