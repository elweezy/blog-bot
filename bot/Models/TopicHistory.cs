using System.Text.Json;

namespace BlogBot.Models;

/// <summary>
/// Legacy topic history (meta/topics_history.json). Read-only: new posts are tracked through their front matter.
/// </summary>
public sealed class TopicHistory
{
    public List<HistoryItem> Items { get; set; } = new();

    public sealed class HistoryItem
    {
        public string TopicId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public List<string> SourceUrls { get; set; } = new();
        public DateTime UsedAtUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static TopicHistory Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TopicHistory();
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Warning: failed to read topic history file '{path}': {ex.Message}. Starting fresh.");
            return new TopicHistory();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Empty file – treat as no history yet
            return new TopicHistory();
        }

        try
        {
            var history = JsonSerializer.Deserialize<TopicHistory>(text, JsonOptions);
            return history ?? new TopicHistory();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Warning: invalid JSON in topic history file '{path}': {ex.Message}. Reinitializing.");
            return new TopicHistory();
        }
    }

}
