namespace BlogBot.Models;

/// <summary>
/// One item for the verifier: a sentence, diagram or teaser, plus the source passages it is supposed to rest on.
/// </summary>
public sealed record ClaimCheck(int Id, string Kind, string Text, IReadOnlyList<string> Evidence);

public sealed class ClaimVerdict
{
    public int Id { get; set; }
    public bool Supported { get; set; }
    public string Reason { get; set; } = string.Empty;
}
