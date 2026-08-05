using System.Runtime.InteropServices;

namespace Zdtllm.Cli.Tui;

/// <summary>
/// Win32 console-mode interop, used only to turn OFF QuickEdit. On Windows, selecting text with the
/// mouse puts the console into "mark" mode, which SUSPENDS the application's stdout — every
/// <c>Console.Write</c> blocks — until the selection is cleared with Enter/Esc. To the user the whole
/// TUI looks frozen (the status clock stops, output stalls) and "unfreezes" the moment they hit Enter.
/// Clearing <c>ENABLE_QUICK_EDIT_MODE</c> stops that; the original mode is restored on exit.
/// </summary>
internal static class NativeConsoleMode
{
    public const int STD_INPUT_HANDLE = -10;
    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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
