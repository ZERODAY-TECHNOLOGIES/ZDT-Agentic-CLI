using System.Runtime.InteropServices;

namespace Zdtllm.Cli.Tui;

/// <summary>
/// Win32 console-mode interop, used only to turn OFF QuickEdit. In the LEGACY Windows console host
/// (conhost), selecting text with the mouse puts the console into "mark" mode, which SUSPENDS the
/// application's stdout — every <c>Console.Write</c> blocks — until the selection is cleared with
/// Enter/Esc. To the user the whole TUI looks frozen (the status clock stops, output stalls) and
/// "unfreezes" the moment they hit Enter. Clearing <c>ENABLE_QUICK_EDIT_MODE</c> stops that.
///
/// But modern terminals (Windows Terminal, VS Code, ConEmu, WezTerm, Alacritty) do their OWN UI-side
/// selection that never blocks output — and they need QuickEdit left ON for click-drag to select at
/// all. Disabling it there only robs the user of copy/paste for no benefit. So <see cref="ShouldDisableQuickEdit()"/>
/// gates the change to the legacy host only; the original mode is restored on exit either way.
/// </summary>
internal static class NativeConsoleMode
{
    public const int STD_INPUT_HANDLE = -10;
    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// Pure policy (testable): should we turn QuickEdit OFF? Only the legacy conhost suspends output
    /// on a mouse selection, so disable there; in a modern terminal leave it ON so the user keeps
    /// click-drag selection (which never freezes output there). Explicit env overrides win.
    /// </summary>
    internal static bool ShouldDisableQuickEdit(
        bool isWindows, bool modernTerminal, bool keepOverride, bool forceDisableOverride)
    {
        if (!isWindows) return false;
        if (keepOverride) return false;         // ZDT_TUI_KEEP_QUICKEDIT — always keep selection
        if (forceDisableOverride) return true;  // ZDT_TUI_NO_QUICKEDIT  — always disable
        return !modernTerminal;
    }

    /// <summary>
    /// Known modern terminals whose selection is UI-side (no output-suspend) and which rely on
    /// QuickEdit being ON for click-drag selection — detected via the env vars they each set.
    /// Anything else (a bare conhost window) is treated as legacy.
    /// </summary>
    internal static bool IsModernTerminal(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        static bool Has(Func<string, string?> e, string k) => !string.IsNullOrEmpty(e(k));
        return Has(env, "WT_SESSION") || Has(env, "WT_PROFILE_ID")                                   // Windows Terminal
            || string.Equals(env("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase)      // VS Code
            || Has(env, "ConEmuPID")                                                                 // ConEmu / Cmder
            || Has(env, "WEZTERM_PANE")                                                              // WezTerm
            || Has(env, "ALACRITTY_WINDOW_ID");                                                      // Alacritty
    }

    /// <summary>Runtime wrapper: reads process env + OS to decide. Used by the TUI at startup.</summary>
    internal static bool ShouldDisableQuickEdit()
    {
        static bool Flag(string k) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
            && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        return ShouldDisableQuickEdit(
            isWindows: OperatingSystem.IsWindows(),
            modernTerminal: IsModernTerminal(Environment.GetEnvironmentVariable),
            keepOverride: Flag("ZDT_TUI_KEEP_QUICKEDIT"),
            forceDisableOverride: Flag("ZDT_TUI_NO_QUICKEDIT"));
    }

    // DllImport (not LibraryImport) so no <AllowUnsafeBlocks> is required. SYSLIB1054 is the analyzer's
    // "prefer LibraryImport" suggestion — suppressed here so TreatWarningsAsErrors doesn't fail on it.
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
#pragma warning restore SYSLIB1054
}
