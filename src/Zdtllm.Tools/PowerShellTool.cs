using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

/// <summary>
/// Runs a command through PowerShell directly (PowerShell 7 <c>pwsh</c> if present, else Windows
/// PowerShell 5.1 <c>powershell.exe</c>) — a first-class alternative to shelling out through Bash on
/// Windows. The command is handed over via <c>-EncodedCommand</c> (base64 UTF-16LE), which sidesteps all
/// of PowerShell's notoriously fragile quote handling, so any script text runs verbatim. Working directory
/// persists across calls within a session; environment variables do NOT. Windows-only (registered only on
/// Windows in Program.cs).
/// </summary>
public sealed class PowerShellTool : ITool
{
    private const string CwdMarker = "__ZDT_CWD_MARKER__";
    private static readonly Lazy<string> ResolvedExe = new(ResolveExecutable);

    private string _cwd;

    public PowerShellTool(string initialCwd)
    {
        ArgumentException.ThrowIfNullOrEmpty(initialCwd);
        _cwd = initialCwd;
    }

    internal static string ExecutablePath => ResolvedExe.Value;

    private static string ResolveExecutable()
    {
        // Prefer PowerShell 7 (pwsh) when installed — cross-platform, better defaults — else fall back to
        // Windows PowerShell 5.1, which is always present on Windows.
        var onPath = FindOnPath("pwsh.exe") ?? FindOnPath("pwsh");
        return onPath ?? "powershell.exe";
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return null;
    }

    public ToolSchema Schema { get; } = new(
        Name: "PowerShell",
        Description: "Execute a PowerShell command/script on Windows (pwsh if available, else Windows " +
            "PowerShell 5.1). The working directory persists across invocations within a session, but " +
            "environment variables do NOT. Prefer this over Bash for Windows- or PowerShell-native tasks.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "The PowerShell command or script to run." },
                timeout = new { type = "integer", description = "Timeout in milliseconds (default 120000)." },
            },
            required = new[] { "command" },
        }));

    public string CurrentWorkingDirectory => _cwd;

    /// <summary>PowerShell mutates _cwd — concurrent calls to the same instance race.</summary>
    public bool CanRunInParallel => false;

    public ITool CloneForSubagent() => new PowerShellTool(_cwd);

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("command", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("PowerShell: missing 'command' parameter.");

        var command = cmdProp.GetString()!;
        var timeoutMs = ShellExec.ResolveTimeoutMs(args);

        // cd to the tracked dir, run the command, capture the native exit code, then print a marker and
        // the (possibly changed) location so cd persists. $LASTEXITCODE is only set by native commands —
        // a cmdlet-only command leaves it $null, so we exit 0 unless a native command failed.
        var cwdLiteral = _cwd.Replace("'", "''");
        var script =
            $"Set-Location -LiteralPath '{cwdLiteral}'\n" +
            command + "\n" +
            "$__zdt_ec = $LASTEXITCODE\n" +
            "Write-Output ''\n" +
            $"Write-Output '{CwdMarker}'\n" +
            "(Get-Location).Path\n" +
            "if ($null -ne $__zdt_ec) { exit $__zdt_ec }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var arguments = new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded,
        };

        (string Stdout, string Stderr, int ExitCode, bool TimedOut) r;
        try
        {
            r = await ShellExec.RunAsync(ExecutablePath, arguments, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"PowerShell: failed to launch ({ex.Message}). Ensure PowerShell is installed.");
        }

        if (r.TimedOut) return ToolResult.Error($"PowerShell: command timed out after {timeoutMs}ms.");

        var (output, newCwd) = ShellExec.ExtractTrailingCwd(r.Stdout, CwdMarker);
        if (newCwd is not null && Directory.Exists(newCwd)) _cwd = newCwd;
        return ShellExec.BuildResult(output, r.Stderr, r.ExitCode);
    }
}
