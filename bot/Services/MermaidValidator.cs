using System.Diagnostics;

namespace BlogBot.Services;

/// <summary>
/// Renders Mermaid diagrams with mermaid-cli (mmdc) to catch syntax errors before a post is published.
/// Set MMDC to the executable (default "mmdc") and MERMAID_PUPPETEER_CONFIG to a puppeteer config file
/// (CI needs {"args":["--no-sandbox"]}).
/// </summary>
public sealed class MermaidValidator
{
    private readonly string _mmdc = Environment.GetEnvironmentVariable("MMDC") ?? "mmdc";
    private readonly string? _puppeteerConfig = Environment.GetEnvironmentVariable("MERMAID_PUPPETEER_CONFIG");
    private bool? _available;

    public bool IsAvailable => _available ??= Probe();

    /// <summary>
    /// Returns null when the diagram renders, otherwise the error output from mmdc.
    /// </summary>
    public async Task<string?> ValidateAsync(string diagram, CancellationToken ct = default)
    {
        var dir = Directory.CreateTempSubdirectory("mermaid-");
        try
        {
            var input = Path.Combine(dir.FullName, "diagram.mmd");
            await File.WriteAllTextAsync(input, diagram, ct);

            var args = new List<string> { "-i", input, "-o", Path.Combine(dir.FullName, "diagram.svg"), "-q" };
            if (!string.IsNullOrWhiteSpace(_puppeteerConfig))
                args.AddRange(["-p", _puppeteerConfig]);

            var (exitCode, output) = await RunAsync(args, TimeSpan.FromSeconds(60), ct);
            return exitCode == 0 ? null : FirstLines(output, 12);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private bool Probe()
    {
        try
        {
            var (exitCode, _) = RunAsync(["--version"], TimeSpan.FromSeconds(30), CancellationToken.None)
                .GetAwaiter().GetResult();
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_mmdc)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // The renderer runs a headless browser on model-written diagrams; it has no business seeing API keys or tokens.
        foreach (var key in psi.Environment.Keys.Where(k =>
                     k.Contains("KEY", StringComparison.OrdinalIgnoreCase) ||
                     k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                     k.Contains("SECRET", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {_mmdc}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"mmdc timed out after {timeout.TotalSeconds:0}s");
        }

        return (process.ExitCode, (await stderr) + (await stdout));
    }

    // Keep the parse error, drop the puppeteer stack trace.
    private static string FirstLines(string text, int count) =>
        string.Join('\n', text.Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeWhile(l => !l.TrimStart().StartsWith("at ") && !l.StartsWith("Parser."))
            .Take(count));
}
