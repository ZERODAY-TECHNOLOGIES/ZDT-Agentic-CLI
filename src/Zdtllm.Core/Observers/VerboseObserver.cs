namespace Zdtllm.Core.Observers;

/// <summary>
/// Pretty-prints tool dispatch + result events to a sink (typically stderr) in the
/// brand palette. Activated by --verbose. Text deltas are NOT echoed because the
/// agent already streams them to stdout / the rich console — duplicating them on
/// stderr would clutter without adding info.
/// </summary>
public sealed class VerboseObserver : IAgentObserver
{
    private readonly TextWriter _sink;

    public VerboseObserver(TextWriter sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public async Task OnToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        await _sink.WriteLineAsync(
                $"  {Palette.Cyan($"→ {toolName}")} {Palette.Mute(Truncate(argumentsJson, 200))}")
            .ConfigureAwait(false);
    }

    public async Task OnToolResultAsync(string toolName, string content, bool isError, TimeSpan duration, CancellationToken ct)
    {
        var marker = isError ? Palette.Red("✗") : Palette.Cyan("✓");
        var preview = Truncate(content.Replace("\n", " "), 200);
        await _sink.WriteLineAsync(
                $"  {marker} {Palette.Mute($"{toolName} ({duration.TotalMilliseconds:F0} ms)")} " +
                $"{Palette.Body(preview)}")
            .ConfigureAwait(false);
    }

    public async Task OnFinalAsync(string finalText, int turns, int? promptTokens, int? completionTokens, CancellationToken ct)
    {
        var pieces = new List<string> { $"turn(s): {turns}" };
        if (promptTokens is int p) pieces.Add($"prompt: {p}");
        if (completionTokens is int c) pieces.Add($"completion: {c}");
        await _sink.WriteLineAsync(Palette.Mute("  " + string.Join("  |  ", pieces))).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
