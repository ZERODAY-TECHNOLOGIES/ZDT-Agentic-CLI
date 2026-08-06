using System.Reflection;
using System.Text;

namespace Zdtllm.Cli;

/// <summary>
/// Best-effort persistent crash log. zdt's bottom-input TUI resets the screen on teardown, which can
/// wipe a last-second <c>zdt: &lt;message&gt;</c> printed to stderr — so an unhandled failure reads as an
/// abrupt, message-less exit, and (worse) <see cref="Program.Main"/> only ever printed <c>ex.Message</c>,
/// dropping the stack trace entirely. This writes the FULL exception (type, message, stack, inner
/// exceptions) to <c>~/.zdtllm/logs/crash-&lt;timestamp&gt;.log</c> so the cause survives, and returns the
/// path so callers can point the user at it. Never throws — diagnostics must not mask the real failure.
/// </summary>
internal static class CrashLog
{
    internal static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zdtllm", "logs");

    /// <summary>Write a crash log under the default (<c>~/.zdtllm/logs</c>) root. Returns the path, or null.</summary>
    public static string? Write(Exception ex, string source) => Write(ex, source, DefaultRoot());

    /// <summary>Write a crash log under an explicit root (used by tests). Returns the path, or null on failure.</summary>
    internal static string? Write(Exception ex, string source, string root)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(ex);
            Directory.CreateDirectory(root);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var path = Path.Combine(root, $"crash-{stamp}.log");
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

            var sb = new StringBuilder();
            sb.Append("zdt ").Append(version).Append(" — ").Append(source)
              .Append(" — ").AppendLine(DateTime.Now.ToString("u"));
            sb.Append("OS: ").AppendLine(Environment.OSVersion.VersionString);
            sb.Append("CommandLine: ").AppendLine(Environment.CommandLine);
            sb.AppendLine();
            sb.AppendLine(ex.ToString()); // type + message + stack + inner exceptions

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null; // logging must never mask the original failure
        }
    }

    /// <summary>
    /// Register process-wide handlers so a fault on ANY thread — a background workflow/agent task, the
    /// status ticker, a fire-and-forget — is captured, not just exceptions that unwind to Main. Both are
    /// best-effort: the unhandled handler can't stop the process, but at least the cause is on disk.
    /// </summary>
    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(ex, "AppDomain.UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            Write(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved(); // logged — don't let it escalate
        };
    }
}
