namespace Zdtllm.Cli;

/// <summary>
/// Signals "the agent is working" vs "waiting for you" through the terminal itself — the same idea
/// as claude-code's animated CMD icon:
///
/// <list type="bullet">
/// <item><b>Taskbar progress</b> (OSC 9;4, the ConEmu/Windows-Terminal convention): an indeterminate
/// spinner on the taskbar/tab icon while a turn runs, cleared when idle.</item>
/// <item><b>Tab title</b> (OSC 0): "⏳ zdt — working" during a turn, "zdt — ready" when waiting.</item>
/// <item><b>A bell</b> on the working→idle transition so the taskbar flashes when zdt finishes and
/// needs your input — even if the window is in the background.</item>
/// </list>
///
/// All out-of-band escape codes: they touch the title / taskbar only, never the screen content or
/// cursor, so they're safe to emit at any time (including while the bottom-input TUI owns the
/// screen). Terminals that don't understand them simply ignore them. Enabled only on an interactive
/// TTY; a no-op otherwise.
/// </summary>
internal static class TerminalStatus
{
    private static volatile bool _enabled;
    private static volatile bool _working;

    /// <summary>Test seam: when set, sequences are written here instead of the real console.</summary>
    internal static TextWriter? Sink;

    /// <summary>Turn the indicators on (interactive TTY only) and set the initial idle title.</summary>
    public static void Enable()
    {
        _enabled = true;
        _working = false;
        SetIdleTitleOnly();
    }

    /// <summary>Agent started working: indeterminate taskbar progress + a "working" title.</summary>
    // OSC sequences are terminated with ST (ESC \), NOT BEL, so a standalone BEL is unambiguously
    // "the bell" (used for the taskbar flash) and never an OSC terminator.
    private const string St = "\x1b\\";
    private const string Bel = "\x07";

    public static void Working()
    {
        if (!_enabled) return;
        _working = true;
        // OSC 9;4;<state>;<progress> — state 3 = indeterminate (animated), progress ignored.
        Write($"\x1b]9;4;3;0{St}");
        SetTitle("⏳ zdt — working");
    }

    /// <summary>Agent finished / waiting for input: clear progress, "ready" title, flash the taskbar.</summary>
    public static void Idle()
    {
        if (!_enabled) return;
        Write($"\x1b]9;4;0;0{St}");   // clear taskbar progress
        SetTitle("zdt — ready");
        if (_working)
        {
            _working = false;
            Write(Bel);               // bell → taskbar flashes: "your turn"
        }
    }

    /// <summary>On shutdown: drop the progress and leave a neutral title.</summary>
    public static void Clear()
    {
        if (!_enabled) return;
        _enabled = false;
        _working = false;
        Write($"\x1b]9;4;0;0{St}");
        Write($"\x1b]0;zdt{St}");
    }

    private static void SetIdleTitleOnly()
    {
        if (!_enabled) return;
        Write($"\x1b]9;4;0;0{St}");
        SetTitle("zdt — ready");
    }

    private static void SetTitle(string title) => Write($"\x1b]0;{title}{St}");

    private static void Write(string s)
    {
        try { var w = Sink ?? Console.Out; w.Write(s); w.Flush(); }
        catch { /* best-effort: a terminal that rejects it just won't show the indicator */ }
    }
}
