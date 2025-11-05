using System.Text.Json;

namespace BlogBot.Models;

public sealed class TopicHistory
{
    public List<HistoryItem> Items { get; set; } = new();

    public sealed class HistoryItem
    {
        public string TopicId { get; set; } = string.Empty;
        public DateTime UsedAtUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public bool IsRecentlyUsed(string topicId, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return Items.Any(i => i.TopicId == topicId && i.UsedAtUtc >= cutoff);
    }

    public void Add(string topicId)
    {
        Items.Add(new HistoryItem
        {
            TopicId = topicId,
            UsedAtUtc = DateTime.UtcNow
        });
    }

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

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}
