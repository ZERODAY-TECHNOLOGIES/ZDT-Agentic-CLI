using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

/// <summary>
/// Shared plumbing for the process-backed shell tools (<see cref="PowerShellTool"/>, <see cref="CmdTool"/>):
/// launch a child process, capture stdout/stderr with a timeout + tree-kill, parse the <c>timeout</c> arg,
/// pull a trailing working-directory marker, and format the tool result. Kept separate from
/// <see cref="BashTool"/> (which has its own MSYS path-translation) so those tools stay small and BashTool
/// stays untouched. Native paths only — PowerShell and cmd both use <c>C:\…</c>, no /c/ translation.
/// </summary>
internal static class ShellExec
{
    public const int DefaultTimeoutMs = 120_000;
    public const int MaxOutputBytes = 256 * 1024;

    public static int ResolveTimeoutMs(JsonElement args)
    {
        if (!args.TryGetProperty("timeout", out var t)) return DefaultTimeoutMs;
        var resolved = t.ValueKind switch
        {
            JsonValueKind.Number when t.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(t.GetString(), out var s) => s,
            _ => DefaultTimeoutMs,
        };
        return Math.Max(1, resolved);
    }

    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/>, returning captured output and
    /// exit code. On timeout the whole process tree is killed and <c>TimedOut</c> is true. Throws only if
    /// the process cannot be started (the caller turns that into a tool error).
    /// </summary>
    public static async Task<(string Stdout, string Stderr, int ExitCode, bool TimedOut)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already dead */ }
            // Observe the cancelled reads so they don't surface as UnobservedTaskException later.
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { /* expected */ }
            return (string.Empty, string.Empty, -1, true);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (stdout, stderr, process.ExitCode, false);
    }

    /// <summary>
    /// Split off a trailing <c>&lt;marker&gt;\n&lt;path&gt;</c> the shell wrapper appended so the caller can
    /// track cd across calls. Returns the output with that tail removed, plus the reported path (or null).
    /// </summary>
    public static (string Output, string? NewCwd) ExtractTrailingCwd(string stdout, string marker)
    {
        var lines = stdout.Split('\n');
        var markerLine = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Trim() == marker) { markerLine = i; break; }
        }
        if (markerLine < 0) return (stdout, null);

        string? newCwd = null;
        if (markerLine + 1 < lines.Length)
        {
            var candidate = lines[markerLine + 1].TrimEnd('\r').Trim();
            if (candidate.Length > 0) newCwd = candidate;
        }
        var output = string.Join('\n', lines.Take(markerLine)).TrimEnd();
        return (output, newCwd);
    }

    public static ToolResult BuildResult(string stdout, string stderr, int exitCode)
    {
        var sb = new StringBuilder();
        if (stdout.Length > 0)
        {
            sb.Append(Truncate(stdout));
            if (!stdout.EndsWith('\n')) sb.Append('\n');
        }
        if (stderr.Length > 0)
        {
            sb.AppendLine("--- stderr ---");
            sb.Append(Truncate(stderr));
            if (!stderr.EndsWith('\n')) sb.Append('\n');
        }
        if (exitCode != 0)
            sb.AppendLine($"(exit code: {exitCode})");

        var content = sb.ToString();
        return exitCode == 0 ? ToolResult.Success(content) : ToolResult.Error(content);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxOutputBytes ? s : s[..MaxOutputBytes] + "\n[output truncated]";
}
