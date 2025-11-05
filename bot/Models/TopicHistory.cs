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

    public bool IsRecentlyUsed(string topicId, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return Items.Any(i => i.TopicId == topicId && i.UsedAtUtc >= cutoff);
    }

    public void Add(string topicId)
    {
        Items.Add(new HistoryItem { TopicId = topicId, UsedAtUtc = DateTime.UtcNow });
    }

    public static TopicHistory Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TopicHistory();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TopicHistory>(json) ?? new TopicHistory();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }
}
