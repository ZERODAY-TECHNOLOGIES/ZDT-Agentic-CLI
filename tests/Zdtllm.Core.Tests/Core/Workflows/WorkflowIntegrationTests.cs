using System.Net;
using System.Text;
using Zdtllm.Core;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.Core.Workflows;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Workflows;

/// <summary>
/// End-to-end: a real WorkflowRunner driving a real SubagentRunner + AgentLoop, with only the
/// LiteLLM HTTP layer stubbed. Proves the whole chain works together — fan-out spawns one real
/// subagent per item, each is a genuine model turn, and a later phase's prompt actually carries the
/// earlier phase's outputs through real request serialization.
/// </summary>
public sealed class WorkflowIntegrationTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static string FinalSse(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Two_phase_workflow_fans_out_then_synthesizes_through_the_real_stack()
    {
        // 3 model turns in order: two Review subagents (sequential fan-out), then one Synthesize.
        var handler = new StubHandler(
            Sse(FinalSse("REVIEW-A")),
            Sse(FinalSse("REVIEW-B")),
            Sse(FinalSse("SYNTH-DONE")));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var parent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 });
        var wfRunner = new WorkflowRunner(new SubagentRunner(parent));

        var workflow = new WorkflowDefinition(
            Name: "review",
            Description: "review each file then synthesize",
            Inputs: new[] { "files" },
            Phases: new[]
            {
                new WorkflowPhase("Review", "general-purpose", "review {{item}}", ForEach: "files", Parallel: false, MaxTurns: 5),
                new WorkflowPhase("Synthesize", "general-purpose", "synthesize:\n{{Review.results}}", ForEach: null, Parallel: false, MaxTurns: 5),
            });

        var result = await wfRunner.RunAsync(
            workflow,
            new Dictionary<string, string> { ["files"] = "a.cs, b.cs" },
            TextWriter.Null,
            CancellationToken.None,
            maxParallel: 0,
            parentModel: "test-model");

        // Three real subagent turns happened.
        handler.Requests.Should().HaveCount(3);

        // Fan-out captured each subagent's output.
        result.Phases[0].Title.Should().Be("Review");
        result.Phases[0].Outputs.Should().Equal("REVIEW-A", "REVIEW-B");

        // The synthesize turn's request body carried BOTH review outputs (templating flowed through
        // real serialization), and the final output is the synthesize turn's text.
        var synthBody = handler.RequestBodies[2];
        synthBody.Should().Contain("synthesize:");
        synthBody.Should().Contain("REVIEW-A");
        synthBody.Should().Contain("REVIEW-B");
        result.FinalOutput.Should().Be("SYNTH-DONE");
    }
}
