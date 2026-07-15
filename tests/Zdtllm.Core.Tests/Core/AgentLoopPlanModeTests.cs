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
/// Plan mode at the loop level: mutating tools are refused (never executed), read-only tools run
/// normally, and the user prompt is grounded with a plan reminder while plan mode is on.
/// </summary>
public sealed class AgentLoopPlanModeTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry, IPlanModeSwitch plan)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 }, planMode: plan);
    }

    private static string ToolCallRound(string name, string argsJson) =>
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
            "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
            $"\"function\":{{\"name\":\"{name}\",\"arguments\":{JsonSerializer.Serialize(argsJson)}}}}}]}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: [DONE]\n\n";

    private static string FinalRound(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Mutating_tool_is_blocked_and_not_executed_in_plan_mode()
    {
        var handler = new StubHandler(
            Sse(ToolCallRound("Write", "{\"path\":\"x.txt\",\"content\":\"hi\"}")),
            Sse(FinalRound("understood")));

        var registry = new ToolRegistry();
        var write = new CountingTool("Write");
        registry.Register(write);

        var agent = BuildAgent(handler, registry, new PlanModeState(active: true));
        await agent.RunOneShotAsync("do it", new StringWriter(), new StringWriter());

        write.Invocations.Should().Be(0);
        handler.RequestBodies[1].Should().Contain("plan mode is ON");
    }

    [Fact]
    public async Task Read_only_tool_runs_normally_in_plan_mode()
    {
        var handler = new StubHandler(
            Sse(ToolCallRound("Peek", "{}")),
            Sse(FinalRound("done")));

        var registry = new ToolRegistry();
        var peek = new CountingTool("Peek"); // not in the blocked set
        registry.Register(peek);

        var agent = BuildAgent(handler, registry, new PlanModeState(active: true));
        await agent.RunOneShotAsync("look around", new StringWriter(), new StringWriter());

        peek.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Plan_reminder_is_appended_to_the_user_prompt_when_active()
    {
        var handler = new StubHandler(Sse(FinalRound("ok")));
        var agent = BuildAgent(handler, new ToolRegistry(), new PlanModeState(active: true));

        await agent.RunOneShotAsync("build the feature", new StringWriter(), new StringWriter());

        handler.RequestBodies[0].Should().Contain("plan mode is ON");
    }

    [Fact]
    public async Task No_reminder_when_plan_mode_is_off()
    {
        var handler = new StubHandler(Sse(FinalRound("ok")));
        var agent = BuildAgent(handler, new ToolRegistry(), new PlanModeState(active: false));

        await agent.RunOneShotAsync("build the feature", new StringWriter(), new StringWriter());

        handler.RequestBodies[0].Should().NotContain("plan mode is ON");
    }

    private sealed class CountingTool : ITool
    {
        private readonly string _name;
        public int Invocations { get; private set; }

        public CountingTool(string name) => _name = name;

        public ToolSchema Schema => new(_name, $"{_name} tool.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(ToolResult.Success("ran"));
        }
    }
}
