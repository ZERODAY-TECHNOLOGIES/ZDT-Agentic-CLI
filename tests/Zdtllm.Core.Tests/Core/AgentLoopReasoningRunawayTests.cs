using System.Net;
using System.Text;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Reasoning-runaway guard: some fine-tuned reasoning models get stuck streaming chain-of-thought
/// forever. Tokens keep flowing, so the idle watchdog never fires and the turn "sits on thinking"
/// indefinitely. <see cref="AgentLoopOptions.MaxReasoningChars"/> caps pure reasoning that produces
/// no visible text / tool call: crossing it aborts the stream and hands off to the reasoning-only
/// recovery, so the turn ALWAYS terminates (nudge → answer, or nudge → fallback).
/// </summary>
public sealed class AgentLoopReasoningRunawayTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static string Reasoning(string s) =>
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"" + s + "\"}}]}\n\n";

    private static string Content(string s) =>
        "data: {\"choices\":[{\"delta\":{\"content\":\"" + s + "\"}}]}\n\n";

    private const string Finish =
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
        "data: [DONE]\n\n";

    // Two 30-char reasoning deltas = 60 chars total. With a 40-char budget the guard trips on the
    // SECOND delta (60 >= 40), before the Finish chunk is ever consumed.
    private static string Runaway() =>
        Reasoning(new string('a', 30)) + Reasoning(new string('b', 30)) + Finish;

    private static AgentLoop Build(int budget, params HttpResponseMessage[] responses)
    {
        var client = new LiteLLMClient(new HttpClient(new StubHandler(responses)),
            new LiteLLMClientOptions
            {
                BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
                InitialBackoff = TimeSpan.FromMilliseconds(1),
            });
        return new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "qwen36", MaxTurns = 5, MaxReasoningChars = budget });
    }

    [Fact]
    public async Task Runaway_reasoning_is_aborted_then_recovers_on_the_nudged_retry()
    {
        // Turn 1 runs away → guard aborts → nudge → retry answers.
        var agent = Build(40, Sse(Runaway()), Sse(Content("Recovered answer.") + Finish));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        result.FinalText.Should().Be("Recovered answer.");
        status.ToString().Should().Contain("reasoning runaway");
        status.ToString().Should().Contain("nudging it to write a visible answer");
    }

    [Fact]
    public async Task Persistent_runaway_terminates_via_the_reasoning_fallback_no_hang()
    {
        // Both the initial turn and the single retry run away. The guard aborts each; recovery fires
        // once (nudge), then surfaces the captured reasoning as a labeled fallback. The turn ends —
        // this is the exact "stuck on thinking forever" case that must not hang.
        var agent = Build(40, Sse(Runaway()), Sse(Runaway()));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        result.FinalText.Should().Contain(new string('a', 30));
        status.ToString().Should().Contain("reasoning runaway");
        status.ToString().Should().Contain("showing the model's reasoning as a fallback");
    }

    [Fact]
    public async Task Budget_zero_disables_the_guard_a_long_think_then_answer_still_works()
    {
        // 60 chars of reasoning far exceeds the 40 used above — but with the guard disabled (0) the
        // model is left to finish, and its eventual visible answer comes through untouched.
        var body = Reasoning(new string('a', 30)) + Reasoning(new string('b', 30))
                 + Content("Fine.") + Finish;
        var agent = Build(0, Sse(body));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        result.FinalText.Should().Be("Fine.");
        status.ToString().Should().NotContain("reasoning runaway");
    }
}
