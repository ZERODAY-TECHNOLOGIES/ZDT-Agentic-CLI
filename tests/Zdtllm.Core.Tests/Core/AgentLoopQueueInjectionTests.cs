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
/// Covers the "type while the model works" feature at the loop level: a message the user queued
/// mid-turn must be folded into the SAME turn at the next tool-round boundary, in both native and
/// XML tool-calling transports, so it reaches the model without waiting for the turn to end.
/// </summary>
public sealed class AgentLoopQueueInjectionTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry, IUserInputQueue queue,
        ToolCallingMode mode = ToolCallingMode.Native)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5, ToolCallingMode = mode },
            inputQueue: queue);
    }

    [Fact]
    public async Task Native_mode_injects_queued_message_after_tool_round()
    {
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());

        var queue = new UserInputQueue();
        queue.Enqueue("also update the changelog");

        var agent = BuildAgent(handler, registry, queue);
        await agent.RunOneShotAsync("please echo", new StringWriter(), new StringWriter());

        // The second model call (after the tool ran) must include the queued message as a user turn.
        var secondBody = handler.RequestBodies[1];
        secondBody.Should().Contain("also update the changelog");
        secondBody.Should().Contain("\"role\":\"user\"");
        queue.HasPending.Should().BeFalse(); // consumed, not left dangling
    }

    [Fact]
    public async Task Native_mode_without_queued_message_does_not_add_extra_user_turn()
    {
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());

        var agent = BuildAgent(handler, registry, new UserInputQueue());
        var result = await agent.RunOneShotAsync("please echo", new StringWriter(), new StringWriter());

        result.FinalText.Should().Be("ok");
        // Empty queue → the injection block is skipped; the only user turn is the original prompt.
        var secondBody = handler.RequestBodies[1];
        System.Text.RegularExpressions.Regex.Matches(secondBody, "\"role\":\"user\"").Count.Should().Be(1);
    }

    [Fact]
    public async Task Xml_mode_folds_queued_message_into_the_synthetic_tool_result_turn()
    {
        // XML mode: assistant emits a <function_calls> block; tool results become one synthetic
        // user turn. A queued message must ride along in THAT message (no second user turn).
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"" +
            "<function_calls><invoke name=\\\"Echo\\\"><parameter name=\\\"text\\\">x</parameter></invoke></function_calls>" +
            "\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());

        var queue = new UserInputQueue();
        queue.Enqueue("and run the tests too");

        var agent = BuildAgent(handler, registry, queue, ToolCallingMode.Xml);
        await agent.RunOneShotAsync("please echo", new StringWriter(), new StringWriter());

        var secondBody = handler.RequestBodies[1];
        secondBody.Should().Contain("and run the tests too");
        secondBody.Should().Contain("The user also sent this message");
        queue.HasPending.Should().BeFalse();
    }

    private sealed class EchoTool : ITool
    {
        public ToolSchema Schema { get; } = new(
            "Echo", "Echo back the text argument prefixed with 'echoed:'.",
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
