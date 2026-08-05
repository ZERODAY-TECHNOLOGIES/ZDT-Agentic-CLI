using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Agents;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Team mode at the loop level: the orchestrator's mutating tools are hidden from the advertised
/// schema AND refused at dispatch (delegate-to-a-subagent message), read-only tools run normally, and
/// every user turn is grounded with the orchestrator reminder (including the live agent roster).
/// </summary>
public sealed class AgentLoopTeamModeTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static AgentLoop BuildAgent(
        StubHandler handler, ToolRegistry registry, ITeamModeSwitch team, TeamAgentRegistry? agents = null)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 },
            teamMode: team, teamAgents: agents);
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
    public async Task Mutating_tool_is_blocked_and_not_executed_in_team_mode()
    {
        var handler = new StubHandler(
            Sse(ToolCallRound("Write", "{\"path\":\"x.txt\",\"content\":\"hi\"}")),
            Sse(FinalRound("delegating")),
            // The blocked Write is non-delegating work, so the forced-dispatch guard nudges once and
            // re-runs — this third response answers that re-run.
            Sse(FinalRound("dispatched")));

        var registry = new ToolRegistry();
        var write = new CountingTool("Write");
        registry.Register(write);

        var agent = BuildAgent(handler, registry, new TeamModeState(active: true));
        await agent.RunOneShotAsync("do it", new StringWriter(), new StringWriter());

        write.Invocations.Should().Be(0);
        handler.RequestBodies[1].Should().Contain("team mode is ON");
    }

    [Fact]
    public async Task Read_only_tool_runs_normally_in_team_mode()
    {
        var handler = new StubHandler(
            Sse(ToolCallRound("Peek", "{}")),
            Sse(FinalRound("done")),
            // Read-only work with no dispatch trips the forced-dispatch guard once; the third
            // response is the re-run (where a genuine read-only turn may just restate its answer).
            Sse(FinalRound("still read-only")));

        var registry = new ToolRegistry();
        var peek = new CountingTool("Peek"); // not in the blocked set
        registry.Register(peek);

        var agent = BuildAgent(handler, registry, new TeamModeState(active: true));
        await agent.RunOneShotAsync("look around", new StringWriter(), new StringWriter());

        peek.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Blocked_tools_are_hidden_from_the_advertised_schema()
    {
        var handler = new StubHandler(Sse(FinalRound("ok")));

        var registry = new ToolRegistry();
        registry.Register(new CountingTool("Write"));
        registry.Register(new CountingTool("Read"));

        var agent = BuildAgent(handler, registry, new TeamModeState(active: true));
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        // Read is advertised; Write is filtered out of the tool schema (the reminder text mentions the
        // word "Write" but never as a "name":"Write" tool entry).
        handler.RequestBodies[0].Should().Contain("\"name\":\"Read\"");
        handler.RequestBodies[0].Should().NotContain("\"name\":\"Write\"");
    }

    [Fact]
    public async Task Blocked_tools_are_advertised_when_team_mode_is_off()
    {
        var handler = new StubHandler(Sse(FinalRound("ok")));

        var registry = new ToolRegistry();
        registry.Register(new CountingTool("Write"));

        var agent = BuildAgent(handler, registry, new TeamModeState(active: false));
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        handler.RequestBodies[0].Should().Contain("\"name\":\"Write\"");
    }

    [Fact]
    public async Task Reminder_with_the_roster_is_appended_when_active()
    {
        var handler = new StubHandler(Sse(FinalRound("ok")));
        var agents = new TeamAgentRegistry(new[]
        {
            new AgentDefinition("db-migrator", "runs SQL migrations", null, "p", null),
        });

        var agent = BuildAgent(handler, new ToolRegistry(), new TeamModeState(active: true), agents);
        await agent.RunOneShotAsync("ship the feature", new StringWriter(), new StringWriter());

        handler.RequestBodies[0].Should().Contain("TEAM MODE ON");
        handler.RequestBodies[0].Should().Contain("db-migrator");
    }

    [Fact]
    public async Task No_reminder_when_team_mode_is_off()
    {
        var handler = new StubHandler(Sse(FinalRound("ok")));
        var agent = BuildAgent(handler, new ToolRegistry(), new TeamModeState(active: false));

        await agent.RunOneShotAsync("ship the feature", new StringWriter(), new StringWriter());

        handler.RequestBodies[0].Should().NotContain("TEAM MODE ON");
    }

    [Fact]
    public async Task Answering_without_dispatching_forces_a_delegation_nudge_and_reruns()
    {
        // Model does read-only work then answers with no Agent dispatch → the hard guarantee must
        // inject the forced-dispatch nudge and re-run the turn instead of letting it end.
        var handler = new StubHandler(
            Sse(ToolCallRound("Read", "{}")),   // req0: read-only research
            Sse(FinalRound("here is the answer")), // req1: answers, dispatched nothing → nudge + rerun
            Sse(FinalRound("restating as read-only"))); // req2: the re-run

        var registry = new ToolRegistry();
        registry.Register(new CountingTool("Read"));

        var agent = BuildAgent(handler, registry, new TeamModeState(active: true));
        await agent.RunOneShotAsync("do the task", new StringWriter(), new StringWriter());

        // A third request proves the turn was forced to continue after the non-delegating answer.
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Contain("without dispatching a subagent");
    }

    [Fact]
    public async Task Dispatching_a_subagent_does_not_trigger_the_nudge()
    {
        // A turn that actually calls the Agent tool has delegated — no forced-dispatch nudge.
        var handler = new StubHandler(
            Sse(ToolCallRound("Agent", "{}")), // dispatch
            Sse(FinalRound("integrated the subagent's result")));

        var registry = new ToolRegistry();
        registry.Register(new CountingTool("Agent")); // stands in for the real Task/Agent tool

        var agent = BuildAgent(handler, registry, new TeamModeState(active: true));
        await agent.RunOneShotAsync("ship it", new StringWriter(), new StringWriter());

        handler.RequestBodies.Should().HaveCount(2); // no re-run
        handler.RequestBodies.Should().NotContain(b => b.Contains("without dispatching a subagent"));
    }

    [Fact]
    public async Task Pure_conversational_answer_is_not_forced_to_dispatch()
    {
        // No tool use at all (e.g. a greeting / concept question) → nothing to delegate, no nudge.
        var handler = new StubHandler(Sse(FinalRound("hello")));

        var agent = BuildAgent(handler, new ToolRegistry(), new TeamModeState(active: true));
        await agent.RunOneShotAsync("hi", new StringWriter(), new StringWriter());

        handler.RequestBodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task No_forced_dispatch_when_team_mode_is_off()
    {
        // Same read-then-answer shape, but team mode off → the turn ends normally, no re-run.
        var handler = new StubHandler(
            Sse(ToolCallRound("Read", "{}")),
            Sse(FinalRound("answer")));

        var registry = new ToolRegistry();
        registry.Register(new CountingTool("Read"));

        var agent = BuildAgent(handler, registry, new TeamModeState(active: false));
        await agent.RunOneShotAsync("look", new StringWriter(), new StringWriter());

        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies.Should().NotContain(b => b.Contains("without dispatching a subagent"));
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
