using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

public sealed class BashTool : ITool
{
    private const string CwdMarker = "__ZDT_CWD_MARKER__";
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxOutputBytes = 256 * 1024;

    private static readonly Lazy<string> ResolvedBashPath = new(ResolveBashExecutable);

    private string _cwd;

    public BashTool(string initialCwd)
    {
        ArgumentException.ThrowIfNullOrEmpty(initialCwd);
        _cwd = initialCwd;
    }

    /// <summary>
    /// Path to the bash executable used by all BashTool instances. On Windows this
    /// prefers Git Bash (which uses /c/-style paths) over WSL bash (which uses /mnt/c/),
    /// since our path translation is targeted at the MSYS convention.
    /// </summary>
    internal static string BashExecutablePath => ResolvedBashPath.Value;

    private static string ResolveBashExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "bash";

        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("ProgramFiles") is { } pf)
        {
            candidates.Add(Path.Combine(pf, "Git", "bin", "bash.exe"));
            candidates.Add(Path.Combine(pf, "Git", "usr", "bin", "bash.exe"));
        }
        if (Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { } pfx)
        {
            candidates.Add(Path.Combine(pfx, "Git", "bin", "bash.exe"));
        }
        candidates.Add(@"C:\Program Files\Git\bin\bash.exe");
        candidates.Add(@"C:\Program Files (x86)\Git\bin\bash.exe");

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return "bash"; // Last resort — PATH lookup. Likely WSL on Windows.
    }

    public ToolSchema Schema { get; } = new(
        Name: "Bash",
        Description: "Execute a bash command. The working directory persists across invocations within a session, but environment variables do NOT.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "The bash command to run." },
                timeout = new { type = "integer", description = "Timeout in milliseconds (default 120000)." },
            },
            required = new[] { "command" },
        }));

    public string CurrentWorkingDirectory => _cwd;

    /// <summary>Bash mutates _cwd — concurrent calls to the same instance race.</summary>
    public bool CanRunInParallel => false;

    /// <summary>
    /// Each subagent gets its own BashTool that starts at the parent's CURRENT
    /// working directory. After that, subagent and parent track cd independently.
    /// </summary>
    public ITool CloneForSubagent() => new BashTool(_cwd);

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("command", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Bash: missing 'command' parameter.");

        var command = cmdProp.GetString()!;
        var timeoutMs = ResolveTimeoutMs(args);

        var wrapper = $"cd {ShellQuote(ToShellPath(_cwd))} || exit 1\n{command}\n__ZDT_EXIT=$?\nprintf '\\n%s\\n' '{CwdMarker}'\npwd\nexit $__ZDT_EXIT";

        var psi = new ProcessStartInfo
        {
            FileName = BashExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(wrapper);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Bash: failed to launch shell ({ex.Message}). Ensure 'bash' is on PATH.");
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                // Observe the cancelled stdout/stderr reads so they don't surface as
                // UnobservedTaskException finalizer-thread spam in long-running sessions.
                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
                catch { /* expected — readers were cancelled */ }
                return ToolResult.Error($"Bash: command timed out after {timeoutMs}ms.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            stdout = ExtractCwdAndTrim(stdout);

            return BuildResult(stdout, stderr, process.ExitCode);
        }
    }

    private string ExtractCwdAndTrim(string stdout)
    {
        var lines = stdout.Split('\n');
        var markerLine = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Trim() == CwdMarker)
            {
                markerLine = i;
                break;
            }
        }

        if (markerLine < 0) return stdout;

        if (markerLine + 1 < lines.Length)
        {
            var newCwdShell = lines[markerLine + 1].TrimEnd('\r').Trim();
            if (newCwdShell.Length > 0)
            {
                var newCwdDotNet = FromShellPath(newCwdShell);
                if (Directory.Exists(newCwdDotNet))
                    _cwd = newCwdDotNet;
            }
        }

        return string.Join('\n', lines.Take(markerLine)).TrimEnd();
    }

    /// <summary>
    /// Convert a .NET path to the form a bash interpreter expects. On Windows with
    /// Git Bash / MSYS, that's /c/Users/... rather than C:\Users\...; on Unix it's
    /// the path unchanged.
    /// </summary>
    private static string ToShellPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var p = Path.GetFullPath(path).Replace('\\', '/');
        if (p.Length >= 2 && p[1] == ':')
            p = "/" + char.ToLowerInvariant(p[0]) + p[2..];
        return p;
    }

    /// <summary>
    /// Convert a bash-form path back to its .NET form on Windows. No-op on Unix.
    /// </summary>
    private static string FromShellPath(string shellPath)
    {
        if (!OperatingSystem.IsWindows()) return shellPath;
        if (shellPath.Length >= 3 && shellPath[0] == '/' && shellPath[2] == '/' && char.IsAsciiLetter(shellPath[1]))
            return char.ToUpperInvariant(shellPath[1]) + ":" + shellPath[2..].Replace('/', '\\');
        return shellPath;
    }

    private static ToolResult BuildResult(string stdout, string stderr, int exitCode)
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
        return exitCode == 0
            ? ToolResult.Success(content)
            : ToolResult.Error(content);
    }

    private static string Truncate(string s) =>
        s.Length <= MaxOutputBytes ? s : s[..MaxOutputBytes] + "\n[output truncated]";

    private static string ShellQuote(string s) => "'" + s.Replace("'", @"'\''") + "'";

    private static int ResolveTimeoutMs(JsonElement args)
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

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch { /* already dead */ }
    }
}
