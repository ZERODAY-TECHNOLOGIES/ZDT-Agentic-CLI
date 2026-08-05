using Zdtllm.Cli.Tui;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// QuickEdit-disable policy (the 0.8.40 refinement of the 0.8.38 freeze fix): only the LEGACY Windows
/// console host suspends output on a mouse selection, so we disable QuickEdit there — but a modern
/// terminal (Windows Terminal, VS Code, …) does UI-side selection that never freezes and needs
/// QuickEdit ON to select at all, so we must leave it alone. Env overrides win both ways.
/// </summary>
public sealed class QuickEditPolicyTests
{
    private static Func<string, string?> Env(params (string Key, string Val)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => (string?)p.Val, StringComparer.OrdinalIgnoreCase);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void Legacy_conhost_disables_quickedit()
    {
        // No modern-terminal env markers → treat as legacy conhost → disable QuickEdit (stop the freeze).
        NativeConsoleMode.ShouldDisableQuickEdit(
            isWindows: true, modernTerminal: false, keepOverride: false, forceDisableOverride: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Modern_terminal_keeps_quickedit_so_selection_still_works()
    {
        // Windows Terminal / VS Code / … → selection is UI-side and never freezes; keep QuickEdit ON.
        NativeConsoleMode.ShouldDisableQuickEdit(
            isWindows: true, modernTerminal: true, keepOverride: false, forceDisableOverride: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Keep_override_wins_even_in_legacy_conhost()
    {
        NativeConsoleMode.ShouldDisableQuickEdit(
            isWindows: true, modernTerminal: false, keepOverride: true, forceDisableOverride: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Force_disable_override_wins_even_in_a_modern_terminal()
    {
        NativeConsoleMode.ShouldDisableQuickEdit(
            isWindows: true, modernTerminal: true, keepOverride: false, forceDisableOverride: true)
            .Should().BeTrue();
    }

    [Fact]
    public void Non_windows_never_touches_console_mode()
    {
        NativeConsoleMode.ShouldDisableQuickEdit(
            isWindows: false, modernTerminal: false, keepOverride: false, forceDisableOverride: false)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("WT_SESSION")]
    [InlineData("WT_PROFILE_ID")]
    [InlineData("ConEmuPID")]
    [InlineData("WEZTERM_PANE")]
    [InlineData("ALACRITTY_WINDOW_ID")]
    public void Detects_modern_terminals_by_their_env_markers(string marker)
    {
        NativeConsoleMode.IsModernTerminal(Env((marker, "x"))).Should().BeTrue();
    }

    [Fact]
    public void Detects_vscode_by_term_program()
    {
        NativeConsoleMode.IsModernTerminal(Env(("TERM_PROGRAM", "vscode"))).Should().BeTrue();
        NativeConsoleMode.IsModernTerminal(Env(("TERM_PROGRAM", "Apple_Terminal"))).Should().BeFalse();
    }

    [Fact]
    public void Bare_conhost_has_no_markers()
    {
        NativeConsoleMode.IsModernTerminal(Env()).Should().BeFalse();
    }
}
