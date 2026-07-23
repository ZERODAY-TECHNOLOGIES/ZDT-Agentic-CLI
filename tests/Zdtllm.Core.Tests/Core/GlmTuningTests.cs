using System.Collections.Immutable;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// The 0.8.17 GLM-5.2 quick-wins: per-turn think/ultrathink reasoning escalation, the GLM id
/// heuristic behind the reasoning_effort default, and the reasoning-only nudge that must not create
/// a second consecutive user turn (which strict-alternation templates reject).
/// </summary>
public sealed class GlmTuningTests
{
    [Theory]
    [InlineData("ultrathink this design", "max")]
    [InlineData("please think hard about the edge cases", "high")]
    [InlineData("think about it", "high")]
    [InlineData("just fix the typo", null)]
    [InlineData("I am thinking about lunch", null)] // "thinking" is not the trigger word
    [InlineData("", null)]
    public void DetectThinkingEffortOverride_maps_keywords(string prompt, string? expected)
    {
        AgentLoop.DetectThinkingEffortOverride(prompt).Should().Be(expected);
    }

    [Theory]
    [InlineData("glm-5.2:cloud", true)]
    [InlineData("zhipu/glm-4.6", true)]
    [InlineData("GLM-4.5-Air", true)]
    [InlineData("qwen3-coder", false)]
    [InlineData("gpt-4o", false)]
    [InlineData(null, false)]
    public void LooksLikeGlm_matches_glm_ids(string? model, bool expected)
    {
        ModelHeuristics.LooksLikeGlm(model).Should().Be(expected);
    }

    [Fact]
    public void NudgeAfterReasoningOnly_folds_into_a_trailing_user_turn()
    {
        using var session = Session.NewEphemeral("m");
        session.AddSystem("sys");
        session.AddUser("do the task");

        var before = session.Messages.Count;
        session.NudgeAfterReasoningOnly("write your final answer now");

        // No second user turn — the nudge is folded into the existing one.
        session.Messages.Count.Should().Be(before);
        session.Messages[^1].Role.Should().Be("user");
        session.Messages[^1].Content.Should().Contain("do the task").And.Contain("write your final answer now");
    }

    [Fact]
    public void NudgeAfterReasoningOnly_appends_a_user_turn_after_a_tool_message()
    {
        using var session = Session.NewEphemeral("m");
        session.AddSystem("sys");
        session.AddUser("do the task");
        session.AddAssistant(null, ImmutableArray.Create(new ToolCall("c1", "Read", "{}")));
        session.AddTool("c1", "file contents");

        session.NudgeAfterReasoningOnly("write your final answer now");

        // After a tool message a new user turn is valid (native allows it) — so one is added.
        session.Messages[^1].Role.Should().Be("user");
        session.Messages[^1].Content.Should().Be("write your final answer now");
    }
}
