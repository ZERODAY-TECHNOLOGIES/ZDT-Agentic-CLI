using System.Net;
using System.Text;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// A reasoning-only turn (chain-of-thought but no visible text, no tool calls) must not dead-end
/// with an empty answer. The loop nudges the model once to write a visible answer and retries; if
/// the retry produces text, that becomes the answer. If it is STILL empty, the captured reasoning
/// is surfaced as a labeled fallback. The recovery fires at most once (no loop).
/// </summary>
public sealed class AgentLoopReasoningRecoveryTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static AgentLoop Build(params HttpResponseMessage[] responses)
    {
        var client = new LiteLLMClient(new HttpClient(new StubHandler(responses)),
            new LiteLLMClientOptions
            {
                BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
                InitialBackoff = TimeSpan.FromMilliseconds(1),
            });
        return new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "glm-5.2:cloud", MaxTurns = 5 });
    }

    private const string ReasoningOnly =
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"let me think hard\"}}]}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Reasoning_only_turn_is_nudged_and_recovers_with_the_retry_answer()
    {
        var second =
            "data: {\"choices\":[{\"delta\":{\"content\":\"The answer is 42.\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var agent = Build(Sse(ReasoningOnly), Sse(second));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        result.FinalText.Should().Be("The answer is 42.");
        status.ToString().Should().Contain("nudging it to write a visible answer");
    }

    [Fact]
    public async Task Persistently_reasoning_only_falls_back_to_the_captured_reasoning()
    {
        // Both the initial turn and the single retry emit only reasoning → fall back to reasoning.
        var agent = Build(Sse(ReasoningOnly), Sse(ReasoningOnly));

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        // Recovery fired exactly once, then surfaced the captured reasoning as the answer.
        result.FinalText.Should().Contain("let me think hard");
        status.ToString().Should().Contain("showing the model's reasoning as a fallback");
    }
}
