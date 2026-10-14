using System.Text.Json;

namespace BlogBot.Repository;

/// <summary>
/// Remembers which sources the bot has written from, whatever happened next (published, PR closed, rejected by the
/// fact-check), so the same popular thread isn't picked run after run. The file lives in the GitHub Actions cache,
/// not in git. A hand-edited block list (meta/blocked_sources.txt) excludes sources permanently.
/// </summary>
public sealed class SourceLedger
{
    public const int CooldownDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<string> _blocked;
    private readonly List<Attempt> _attempts;

    public sealed record Attempt(string Url, string Title, DateTime AttemptedAtUtc);

    private SourceLedger(string path, List<Attempt> attempts, List<string> blocked)
    {
        _path = path;
        _attempts = attempts;
        _blocked = blocked;
    }

    public static SourceLedger Load(string ledgerPath, string blockListPath)
    {
        var attempts = new List<Attempt>();
        if (File.Exists(ledgerPath))
        {
            try
            {
                attempts = JsonSerializer.Deserialize<List<Attempt>>(File.ReadAllText(ledgerPath), JsonOptions) ?? [];
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Warning: ignoring unreadable source ledger {ledgerPath}: {ex.Message}");
            }
        }

        var blocked = File.Exists(blockListPath)
            ? File.ReadAllLines(blockListPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(PostIndex.NormalizeUrl)
                .ToList()
            : [];

        Console.WriteLine($"Source ledger: {attempts.Count} past attempts, {blocked.Count} blocked sources.");
        return new SourceLedger(ledgerPath, attempts, blocked);
    }

    /// <summary>Why this URL can't be used right now, or null if it can.</summary>
    public string? Unavailable(string url)
    {
        var normalized = PostIndex.NormalizeUrl(url);
        if (normalized.Length == 0) return null;

        if (_blocked.Any(b => normalized.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
            return "source is on the block list";

        var last = _attempts
            .Where(a => PostIndex.NormalizeUrl(a.Url).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.AttemptedAtUtc)
            .DefaultIfEmpty()
            .Max();
        return last != default && last > DateTime.UtcNow.AddDays(-CooldownDays)
            ? $"source already written from on {last:yyyy-MM-dd} (cooldown {CooldownDays} days)"
            : null;
    }

    /// <summary>Records the sources of the topic about to be written and saves immediately, so failed runs count too.</summary>
    public void Record(IEnumerable<string> urls, string title)
    {
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
            _attempts.Add(new Attempt(url, title, DateTime.UtcNow));

        // Old entries no longer matter; keep the file small.
        _attempts.RemoveAll(a => a.AttemptedAtUtc < DateTime.UtcNow.AddDays(-CooldownDays * 3));

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_attempts, JsonOptions));
    }
}
