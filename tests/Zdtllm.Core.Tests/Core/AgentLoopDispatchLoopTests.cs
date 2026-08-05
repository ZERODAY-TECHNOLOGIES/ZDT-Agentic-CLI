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
/// Orchestrator-level dispatch-loop guard (Grup A). The tool-loop detector keys on result hashes, but
/// a re-dispatched subagent returns a different report every time, so an orchestrator that keeps
/// handing out the SAME task never trips it — and each fresh ephemeral subagent redoes the work. This
/// guard fingerprints Agent dispatches by (subagent_type + normalised prompt): the 3rd identical one
/// is blocked before spawning, and hammering it (or a runaway total) hard-stops the turn with tools
/// dropped so the orchestrator must summarise instead of spinning forever.
/// </summary>
public sealed class AgentLoopDispatchLoopTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static AgentLoop Build(StubHandler handler, ToolRegistry registry, int maxTurns = 20)
    {
        var client = new LiteLLMClient(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = maxTurns });
    }

    private static string AgentCall(string argsJson) =>
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
            "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
            $"\"function\":{{\"name\":\"Agent\",\"arguments\":{JsonSerializer.Serialize(argsJson)}}}}}]}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: [DONE]\n\n";

    private static string Final(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Third_identical_dispatch_is_blocked_before_spawning()
    {
        const string args = "{\"subagent_type\":\"general-purpose\",\"prompt\":\"run the tests and fix failures\"}";
        var handler = new StubHandler(
            Sse(AgentCall(args)),  // #1 runs
            Sse(AgentCall(args)),  // #2 runs
            Sse(AgentCall(args)),  // #3 blocked — never reaches the tool
            Sse(Final("ok, changing strategy")));

        var agentTool = new CountingTool("Agent");
        var registry = new ToolRegistry();
        registry.Register(agentTool);

        await Build(handler, registry).RunOneShotAsync("go", new StringWriter(), new StringWriter());

        agentTool.Invocations.Should().Be(2);
        handler.RequestBodies[3].Should().Contain("[dispatch-loop]");
    }

    [Fact]
    public async Task Cosmetically_reworded_but_same_task_still_collides()
    {
        // Different whitespace/case, same normalised prompt → same fingerprint → 3rd is still blocked.
        var handler = new StubHandler(
            Sse(AgentCall("{\"subagent_type\":\"general-purpose\",\"prompt\":\"Run the tests\"}")),
            Sse(AgentCall("{\"subagent_type\":\"general-purpose\",\"prompt\":\"run   the tests\"}")),
            Sse(AgentCall("{\"subagent_type\":\"general-purpose\",\"prompt\":\"run the tests\\n\\n\"}")),
            Sse(Final("done")));

        var agentTool = new CountingTool("Agent");
        var registry = new ToolRegistry();
        registry.Register(agentTool);

        await Build(handler, registry).RunOneShotAsync("go", new StringWriter(), new StringWriter());

        agentTool.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task Distinct_tasks_are_not_blocked()
    {
        var handler = new StubHandler(
            Sse(AgentCall("{\"subagent_type\":\"general-purpose\",\"prompt\":\"task A\"}")),
            Sse(AgentCall("{\"subagent_type\":\"general-purpose\",\"prompt\":\"task B\"}")),
            Sse(AgentCall("{\"subagent_type\":\"general-purpose\",\"prompt\":\"task C\"}")),
            Sse(Final("all three done")));

        var agentTool = new CountingTool("Agent");
        var registry = new ToolRegistry();
        registry.Register(agentTool);

        await Build(handler, registry).RunOneShotAsync("go", new StringWriter(), new StringWriter());

        agentTool.Invocations.Should().Be(3); // three genuinely different sub-tasks all run
    }

    [Fact]
    public async Task Hammering_the_same_task_hard_stops_and_drops_tools()
    {
        const string args = "{\"subagent_type\":\"general-purpose\",\"prompt\":\"fix it\"}";
        // 6 attempts (2 run, 3rd–5th blocked, 6th trips the hard-stop), then one final no-tools round.
        var handler = new StubHandler(
            Sse(AgentCall(args)), Sse(AgentCall(args)), Sse(AgentCall(args)),
            Sse(AgentCall(args)), Sse(AgentCall(args)), Sse(AgentCall(args)),
            Sse(Final("here is my summary")));

        var agentTool = new CountingTool("Agent");
        var registry = new ToolRegistry();
        registry.Register(agentTool);

        var result = await Build(handler, registry).RunOneShotAsync("go", new StringWriter(), new StringWriter());

        agentTool.Invocations.Should().Be(2); // never spawned again after the loop was detected
        handler.RequestBodies[0].Should().Contain("\"tools\":");     // early rounds advertise the Agent tool
        handler.RequestBodies[^1].Should().NotContain("\"tools\":"); // final round ran with all tools stripped
        result.FinalText.Should().Contain("summary");
    }

    [Fact]
    public async Task Second_dispatch_inherits_the_first_subagents_report()
    {
        // Grup B (continuity): re-dispatching the same task folds the first subagent's report into the
        // second's prompt, so the fresh-context subagent continues instead of starting from zero.
        const string args = "{\"subagent_type\":\"general-purpose\",\"prompt\":\"fix the build\"}";
        var handler = new StubHandler(
            Sse(AgentCall(args)),  // #1 — untouched prompt
            Sse(AgentCall(args)),  // #2 — inherits #1's report
            Sse(Final("done")));

        var tool = new RecordingAgentTool();
        var registry = new ToolRegistry();
        registry.Register(tool);

        await Build(handler, registry).RunOneShotAsync("go", new StringWriter(), new StringWriter());

        tool.Prompts.Should().HaveCount(2);
        tool.Prompts[0].Should().Be("fix the build");                            // first: verbatim
        tool.Prompts[0].Should().NotContain("CONTINUING");
        tool.Prompts[1].Should().Contain("CONTINUING A TASK ALREADY ATTEMPTED"); // second: continuity header
        tool.Prompts[1].Should().Contain("REPORT-1");                            // ...carries the prior report
        tool.Prompts[1].Should().Contain("fix the build");                       // ...and the original instructions
    }

    private sealed class RecordingAgentTool : ITool
    {
        public List<string> Prompts { get; } = new();

        public ToolSchema Schema => new("Agent", "Agent tool.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var prompt = args.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String
                ? (p.GetString() ?? "") : "";
            Prompts.Add(prompt);
            return Task.FromResult(ToolResult.Success($"REPORT-{Prompts.Count}"));
        }
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
            // A DIFFERENT report every call — exactly like a real subagent, so the result-hash
            // exact-repeat guard stays silent and the dispatch-fingerprint guard is what fires.
            return Task.FromResult(ToolResult.Success($"subagent report #{Invocations}"));
        }
    }
}
