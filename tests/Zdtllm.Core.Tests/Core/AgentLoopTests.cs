using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

public sealed class AgentLoopTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static AgentLoop BuildAgent(
        StubHandler handler,
        ToolRegistry registry,
        PermissionRuleSet? perms = null,
        bool skipPermissions = false)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client,
            registry,
            perms ?? PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5, SkipPermissions = skipPermissions });
    }

    [Fact]
    public async Task Returns_final_text_when_no_tool_calls()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello there\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(sse));
        var agent = BuildAgent(handler, new ToolRegistry());

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("hi", output, status);

        result.FinalText.Should().Be("Hello there");
        result.Turns.Should().Be(1);
        result.PromptTokens.Should().Be(10);
        output.ToString().Should().Contain("Hello there");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Executes_tool_call_and_continues_to_next_turn()
    {
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        var echo = new EchoTool();
        registry.Register(echo);

        var agent = BuildAgent(handler, registry);

        var output = new StringWriter();
        var status = new StringWriter();
        var result = await agent.RunOneShotAsync("please echo", output, status);

        result.FinalText.Should().Be("done");
        result.Turns.Should().Be(2);
        echo.Invocations.Should().Be(1);
        echo.LastArgs.Should().Be("x");
        handler.Requests.Should().HaveCount(2);

        var secondBody = handler.RequestBodies[1];
        secondBody.Should().Contain("\"role\":\"tool\"");
        secondBody.Should().Contain("\"tool_call_id\":\"c1\"");
        secondBody.Should().Contain("\"content\":\"echoed:x\"");
    }

    [Fact]
    public async Task Permission_deny_returns_denial_string_to_model()
    {
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"forbidden\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"ack\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(round1), Sse(round2));

        var registry = new ToolRegistry();
        var echo = new EchoTool();
        registry.Register(echo);

        // Echo isn't permission-required by default, but a deny rule on the bare tool blocks it.
        var perms = PermissionRuleSet.Build(allow: [], ask: [], deny: ["Echo"]);
        var agent = BuildAgent(handler, registry, perms);

        var output = new StringWriter();
        var status = new StringWriter();
        await agent.RunOneShotAsync("try it", output, status);

        echo.Invocations.Should().Be(0);
        var secondBody = handler.RequestBodies[1];
        secondBody.Should().Contain("Permission denied");
    }

    [Fact]
    public async Task Throws_when_max_turns_exceeded()
    {
        // Every round emits a tool call so the loop never terminates naturally.
        string ToolCallRound(int id) =>
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
            $"{{\"index\":0,\"id\":\"c{id}\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(
            Sse(ToolCallRound(1)),
            Sse(ToolCallRound(2)),
            Sse(ToolCallRound(3)),
            Sse(ToolCallRound(4)),
            Sse(ToolCallRound(5)));

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());
        var agent = BuildAgent(handler, registry);

        var act = async () => await agent.RunOneShotAsync("loop forever", new StringWriter(), new StringWriter());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*max turns*");
    }

    private sealed class EchoTool : ITool
    {
        public int Invocations { get; private set; }
        public string? LastArgs { get; private set; }

        public ToolSchema Schema { get; } = new(
            "Echo",
            "Echo back the text argument prefixed with 'echoed:'.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { text = new { type = "string" } },
                required = new[] { "text" },
            }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Invocations++;
            LastArgs = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            return Task.FromResult(ToolResult.Success($"echoed:{LastArgs}"));
        }
    }
}
