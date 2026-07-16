using Zdtllm.Cli;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// Verifies the working/idle terminal indicators emit the right OSC control sequences at the right
/// transitions. (The actual taskbar rendering is the terminal's job â€” here we assert what zdt
/// writes.) Runs non-parallel because it toggles TerminalStatus's global state + sink.
/// </summary>
[Collection("terminal-status")]
public sealed class TerminalStatusTests : IDisposable
{
    private static readonly string Bel = ((char)7).ToString();
    private readonly StringWriter _sink = new();

    public TerminalStatusTests() => TerminalStatus.Sink = _sink;

    public void Dispose()
    {
        TerminalStatus.Clear();
        TerminalStatus.Sink = null;
    }

    private string Captured => _sink.ToString();

    [Fact]
    public void Enable_sets_an_idle_title_and_clears_progress()
    {
        TerminalStatus.Enable();

        Captured.Should().Contain("]9;4;0;0");   // clear taskbar progress
        Captured.Should().Contain("ready");       // idle title
    }

    [Fact]
    public void Working_emits_the_indeterminate_progress_and_working_title()
    {
        TerminalStatus.Enable();
        _sink.GetStringBuilder().Clear();

        TerminalStatus.Working();

        Captured.Should().Contain("]9;4;3;0");   // indeterminate (animated) progress
        Captured.Should().Contain("working");
    }

    [Fact]
    public void Idle_after_working_clears_progress_and_rings_the_bell()
    {
        TerminalStatus.Enable();
        TerminalStatus.Working();
        _sink.GetStringBuilder().Clear();

        TerminalStatus.Idle();

        Captured.Should().Contain("]9;4;0;0");   // clear progress
        Captured.Should().Contain("ready");       // ready title
        Captured.Should().Contain(Bel);           // BEL -> taskbar flash ("your turn")
    }

    [Fact]
    public void Idle_without_a_prior_working_does_not_ring_the_bell()
    {
        TerminalStatus.Enable();
        _sink.GetStringBuilder().Clear();

        TerminalStatus.Idle();

        Captured.Should().NotContain(Bel);
    }

    [Fact]
    public void Disabled_emits_nothing()
    {
        // No Enable() call -> all no-ops (e.g. non-interactive / print mode).
        TerminalStatus.Working();
        TerminalStatus.Idle();

        Captured.Should().BeEmpty();
    }
}

[CollectionDefinition("terminal-status", DisableParallelization = true)]
public sealed class TerminalStatusCollection { }
