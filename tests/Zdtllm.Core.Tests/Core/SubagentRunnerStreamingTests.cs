using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// The live-activity sink: a real subagent's output + tool status is streamed to the sink, tagged
/// with the agent's label, instead of being buffered and discarded.
/// </summary>
public sealed class SubagentRunnerStreamingTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task Subagent_activity_is_streamed_to_the_sink_tagged_with_its_label()
    {
        // Round 1: the subagent calls Echo. Round 2: it answers "all done".
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"hi\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"all done\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(round1), Sse(round2));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());
        var parent = new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 });

        var sink = new StringWriter();
        var runner = new SubagentRunner(parent, activitySink: sink);

        var result = await runner.RunAsync(
            new SubagentRequest(Description: "MyTask", Prompt: "do it", Type: "general-purpose"),
            CancellationToken.None);

        result.FinalText.Should().Be("all done");

        var trace = sink.ToString();
        trace.Should().Contain("[MyTask #1]");   // tagged with the label
        trace.Should().Contain("Echo");           // the tool it used shows up as activity
        trace.Should().Contain("all done");       // its answer streamed too
    }

    [Fact]
    public async Task Without_a_sink_nothing_is_streamed_and_final_text_still_returns()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"quiet result\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(sse));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var parent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 });

        var runner = new SubagentRunner(parent); // no sink
        var result = await runner.RunAsync(
            new SubagentRequest("T", "go", "general-purpose"), CancellationToken.None);

        result.FinalText.Should().Be("quiet result");
    }

    private sealed class EchoTool : ITool
    {
        public ToolSchema Schema { get; } = new(
            "Echo", "Echo the text.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { text = new { type = "string" } },
                required = new[] { "text" },
            }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var text = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            return Task.FromResult(ToolResult.Success($"echoed:{text}"));
        }
    }
}
