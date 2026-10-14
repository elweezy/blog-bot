namespace BlogBot.Repository;

/// <summary>
/// Optional hand-edited list of sources the bot must never write from (meta/blocked_sources.txt).
/// The bot keeps no other memory of past attempts: it relies on fetching fresh trending posts.
/// </summary>
public sealed class BlockList
{
    private readonly List<string> _prefixes;

    private BlockList(List<string> prefixes) => _prefixes = prefixes;

    public static BlockList Load(string path) => new(File.Exists(path)
        ? File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(PostIndex.NormalizeUrl)
            .ToList()
        : []);

    public bool Blocks(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        _prefixes.Any(p => PostIndex.NormalizeUrl(url).StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
