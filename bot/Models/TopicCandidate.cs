namespace BlogBot.Models;

public sealed class TopicCandidate
{
    public string Title { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // e.g. "hackernews", "devto", "reddit", "huggingface"
    public string Url { get; set; } = string.Empty;
    public string? DiscussionUrl { get; set; } // e.g. the HN thread when Url points to the article
    public string? SourceId { get; set; } // id used to fetch the full text later (dev.to article id, HN story id)
    public string Summary { get; set; } = string.Empty; // short teaser shown to the model during clustering
    public double Popularity { get; set; } // normalized 0..1 within its source
    public DateTime CreatedAt { get; set; }
}
