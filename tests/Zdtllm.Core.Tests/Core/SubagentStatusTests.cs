using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Grup C: SubagentRunner.DetectStatus turns a subagent's final message into a completed/partial/blocked
/// outcome the orchestrator can act on. An explicit trailing STATUS line wins; otherwise a subagent that
/// exhausted its turn budget is treated as partial (ran out of room), else completed.
/// </summary>
public sealed class SubagentStatusTests
{
    [Theory]
    [InlineData("Did the thing.\n\nSTATUS: completed", "completed")]
    [InlineData("Fixed 2 of 3.\nSTATUS: partial — one test still red", "partial")]
    [InlineData("Cannot proceed.\nSTATUS: blocked — needs DB creds", "blocked")]
    [InlineData("STATUS: done", "completed")]
    [InlineData("STATUS: incomplete — ran out of time", "partial")]
    [InlineData("status: BLOCKED — case-insensitive", "blocked")]
    public void Explicit_status_line_is_parsed(string text, string expected)
    {
        SubagentRunner.DetectStatus(text, turns: 3, maxTurns: 25).Should().Be(expected);
    }

    [Fact]
    public void Last_status_line_wins_when_several_are_present()
    {
        SubagentRunner.DetectStatus("STATUS: blocked\n...actually fixed it...\nSTATUS: completed", 3, 25)
            .Should().Be("completed");
    }

    [Fact]
    public void No_status_and_turn_cap_hit_infers_partial()
    {
        SubagentRunner.DetectStatus("just some text, no status line", turns: 25, maxTurns: 25)
            .Should().Be("partial");
    }

    [Fact]
    public void No_status_and_under_the_cap_infers_completed()
    {
        SubagentRunner.DetectStatus("wrapped up cleanly", turns: 5, maxTurns: 25).Should().Be("completed");
    }

    [Fact]
    public void Null_or_empty_text_under_cap_is_completed()
    {
        SubagentRunner.DetectStatus(null, 1, 25).Should().Be("completed");
        SubagentRunner.DetectStatus("", 1, 25).Should().Be("completed");
    }
}
