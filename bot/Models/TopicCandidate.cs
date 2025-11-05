namespace BlogBot.Models;

public sealed class TopicCandidate
{
    public string Title { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // e.g. "stackoverflow", "devto", "reddit"
    public string Url { get; set; } = string.Empty;
    public double Popularity { get; set; } // normalized votes/reactions
    public DateTime CreatedAt { get; set; }
}
