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
/// The interactive allow / always-allow / deny prompt fired when a tool call resolves to Ask.
/// Before this, an Ask decision dead-ended into a synthetic "[Permission required]" text the model
/// just saw fail; now a reachable human is asked and their verdict decides whether the tool runs.
/// </summary>
public sealed class AgentLoopPermissionPromptTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

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

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry, IInteractivePrompter prompter)
    {
        var client = new LiteLLMClient(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        // No rules: a permission-required tool ("Bash") falls through to the default Ask, exactly
        // as in real use — so "always allow" adding an allow rule genuinely overrides it (an
        // explicit ask rule would out-rank allow and never let the grant take effect).
        return new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 }, prompter: prompter);
    }

    [Fact]
    public async Task Ask_tool_runs_when_user_allows_once()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{}")), Sse(FinalRound("done")));
        var registry = new ToolRegistry();
        var danger = new CountingTool("Bash");
        registry.Register(danger);

        var prompter = new FakePrompter("Yes");
        var agent = BuildAgent(handler, registry, prompter);
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        danger.Invocations.Should().Be(1);
        prompter.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Ask_tool_is_blocked_when_user_declines()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{}")), Sse(FinalRound("ok")));
        var registry = new ToolRegistry();
        var danger = new CountingTool("Bash");
        registry.Register(danger);

        var agent = BuildAgent(handler, registry, new FakePrompter("No, tell the model"));
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        danger.Invocations.Should().Be(0);
        handler.RequestBodies[1].Should().Contain("declined");
    }

    [Fact]
    public async Task Always_allow_adds_a_session_rule_and_runs()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{}")), Sse(FinalRound("done")));
        var registry = new ToolRegistry();
        var danger = new CountingTool("Bash");
        registry.Register(danger);

        var agent = BuildAgent(handler, registry, new FakePrompter("Yes, and don't ask again"));
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        danger.Invocations.Should().Be(1);
        agent.Permissions.Evaluate("Bash", null).Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public async Task No_prompt_when_prompter_unavailable_falls_back_to_text_error()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{}")), Sse(FinalRound("ok")));
        var registry = new ToolRegistry();
        var danger = new CountingTool("Bash");
        registry.Register(danger);

        // Print mode / subagents get UnavailablePrompter → no prompt, the historic text error stands.
        var agent = BuildAgent(handler, registry, UnavailablePrompter.Instance);
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        danger.Invocations.Should().Be(0);
        handler.RequestBodies[1].Should().Contain("Permission required");
    }

    private sealed class FakePrompter : IInteractivePrompter
    {
        private readonly string _choice;
        public int Calls { get; private set; }

        public FakePrompter(string choice) => _choice = choice;

        public bool IsAvailable => true;

        public Task<IReadOnlyList<string>> SelectAsync(
            string question, string? header, IReadOnlyList<PromptChoice> options,
            bool multiSelect, bool allowFreeText, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<string>>(new[] { _choice });
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
            return Task.FromResult(ToolResult.Success("ran"));
        }
    }
}
