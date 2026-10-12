namespace BlogBot.Models;

public sealed class Topic
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Angle { get; set; } = string.Empty; // the opinion or question the post argues
    public string PrimaryUrl { get; set; } = string.Empty;
    public List<string> SupportingUrls { get; set; } = new();
    public List<TopicCandidate> Sources { get; set; } = new();
    public double AggregateScore { get; set; }
}
