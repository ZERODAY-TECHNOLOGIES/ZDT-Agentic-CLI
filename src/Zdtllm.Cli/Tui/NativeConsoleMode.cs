using System.Runtime.InteropServices;

namespace Zdtllm.Cli.Tui;

/// <summary>
/// Win32 console-mode interop for QuickEdit. QuickEdit is what lets the user select text with the mouse
/// to copy it — so by default we LEAVE IT ON (every terminal, including the legacy conhost), because
/// selection is what users want most.
///
/// The one cost is legacy-conhost-only: while a selection is active, conhost enters "mark" mode which
/// pauses the app's stdout until the selection is cleared (Enter/Esc/click). That's expected behaviour —
/// you're reading/copying — and output resumes the instant you clear it; Windows Terminal has no such
/// pause (its selection is UI-side). A user who prefers the never-pause behaviour (at the cost of losing
/// selection in conhost) opts in with <c>ZDT_TUI_NO_QUICKEDIT=1</c>, which clears
/// <c>ENABLE_QUICK_EDIT_MODE</c>. The original mode is restored on exit either way.
/// </summary>
internal static class NativeConsoleMode
{
    public const int STD_INPUT_HANDLE = -10;
    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// Pure policy (testable): should we turn QuickEdit OFF? No — by default keep it ON so mouse
    /// text-selection works in every terminal. Only the explicit <c>ZDT_TUI_NO_QUICKEDIT=1</c> opt-out
    /// disables it (trading selection for conhost's never-pause output). Non-Windows: nothing to do.
    /// </summary>
    internal static bool ShouldDisableQuickEdit(bool isWindows, bool forceDisableOverride)
    {
        if (!isWindows) return false;
        return forceDisableOverride; // default false → keep QuickEdit ON → selection works
    }

    /// <summary>Runtime wrapper: reads process env + OS to decide. Used by the TUI at startup. QuickEdit
    /// stays ON (selection works) unless <c>ZDT_TUI_NO_QUICKEDIT=1</c> opts out.</summary>
    internal static bool ShouldDisableQuickEdit()
    {
        static bool Flag(string k) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
            && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        return ShouldDisableQuickEdit(
            isWindows: OperatingSystem.IsWindows(),
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
