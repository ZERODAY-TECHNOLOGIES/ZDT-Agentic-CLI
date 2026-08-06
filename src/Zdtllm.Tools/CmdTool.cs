using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

/// <summary>
/// Runs a command through the Windows Command Prompt (<c>cmd.exe</c>) directly — for classic Windows shell
/// / batch commands. The command is written to a throwaway <c>.bat</c> in %TEMP% and executed with
/// <c>cmd.exe /c</c>: this sidesteps cmd's byzantine command-line quoting entirely (the command sits on its
/// own line, verbatim) and lets us preserve the real exit code and track cd. Working directory persists
/// across calls within a session; environment variables do NOT. Windows-only (registered only on Windows).
/// </summary>
public sealed class CmdTool : ITool
{
    private const string CwdMarker = "__ZDT_CWD_MARKER__";

    private string _cwd;

    public CmdTool(string initialCwd)
    {
        ArgumentException.ThrowIfNullOrEmpty(initialCwd);
        _cwd = initialCwd;
    }

    public ToolSchema Schema { get; } = new(
        Name: "Cmd",
        Description: "Execute a Windows Command Prompt (cmd.exe) command. The working directory persists " +
            "across invocations within a session, but environment variables do NOT. Use for classic " +
            "Windows shell / batch commands (dir, copy, where, set, &&/|| chains, etc.).",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "The cmd.exe command to run." },
                timeout = new { type = "integer", description = "Timeout in milliseconds (default 120000)." },
            },
            required = new[] { "command" },
        }));

    public string CurrentWorkingDirectory => _cwd;

    /// <summary>Cmd mutates _cwd — concurrent calls to the same instance race.</summary>
    public bool CanRunInParallel => false;

    public ITool CloneForSubagent() => new CmdTool(_cwd);

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("command", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("Cmd: missing 'command' parameter.");

        var command = cmdProp.GetString()!;
        var timeoutMs = ShellExec.ResolveTimeoutMs(args);

        // cd to the tracked dir, run the command verbatim, snapshot its errorlevel, then print a marker and
        // the (possibly changed) directory so cd persists — finally re-raise the command's exit code.
        var bat = new StringBuilder();
        bat.Append("@echo off\r\n");
        bat.Append("cd /d \"").Append(_cwd).Append("\"\r\n");
        bat.Append(command).Append("\r\n");
        bat.Append("set __ZDT_EC=%errorlevel%\r\n");
        bat.Append("echo.\r\n");
        bat.Append("echo ").Append(CwdMarker).Append("\r\n");
        bat.Append("cd\r\n");
        bat.Append("exit /b %__ZDT_EC%\r\n");

        var batPath = Path.Combine(Path.GetTempPath(), "zdt-cmd-" + Guid.NewGuid().ToString("N") + ".bat");
        try
        {
            await File.WriteAllTextAsync(batPath, bat.ToString(), ct).ConfigureAwait(false);

            (string Stdout, string Stderr, int ExitCode, bool TimedOut) r;
            try
            {
                r = await ShellExec.RunAsync("cmd.exe", new[] { "/c", batPath }, timeoutMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Cmd: failed to launch cmd.exe ({ex.Message}).");
            }

            if (r.TimedOut) return ToolResult.Error($"Cmd: command timed out after {timeoutMs}ms.");

            var (output, newCwd) = ShellExec.ExtractTrailingCwd(r.Stdout, CwdMarker);
            if (newCwd is not null && Directory.Exists(newCwd)) _cwd = newCwd;
            return ShellExec.BuildResult(output, r.Stderr, r.ExitCode);
        }
        finally
        {
            try { File.Delete(batPath); } catch { /* best effort */ }
        }
    }
}
