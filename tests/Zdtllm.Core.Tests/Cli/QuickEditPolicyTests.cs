using Zdtllm.Cli.Tui;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// QuickEdit-disable policy (0.8.48): keep QuickEdit ON by default in EVERY terminal so mouse text
/// selection works; only the explicit ZDT_TUI_NO_QUICKEDIT=1 opt-out disables it (trading selection for
/// conhost's never-pause output). Non-Windows never touches the console mode.
/// </summary>
public sealed class QuickEditPolicyTests
{
    [Fact]
    public void Keeps_quickedit_on_by_default_so_selection_works()
    {
        // Default (no opt-out) → do NOT disable → QuickEdit stays on → user can select text.
        NativeConsoleMode.ShouldDisableQuickEdit(isWindows: true, forceDisableOverride: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Force_disable_override_opts_out_of_selection()
    {
        // ZDT_TUI_NO_QUICKEDIT=1 → disable QuickEdit (the never-pause behaviour, no selection in conhost).
        NativeConsoleMode.ShouldDisableQuickEdit(isWindows: true, forceDisableOverride: true)
            .Should().BeTrue();
    }

    [Fact]
    public void Non_windows_never_touches_console_mode()
    {
        NativeConsoleMode.ShouldDisableQuickEdit(isWindows: false, forceDisableOverride: true)
            .Should().BeFalse();
    }
}
