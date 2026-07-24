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
/// Permission MODES layered over the rules: AcceptEdits auto-allows file edits (not Bash), Bypass
/// auto-allows everything, and the dangerous-op deny-floor forces an interactive confirm even under
/// Bypass. Rules still win — this only governs a call that would otherwise Ask.
/// </summary>
public sealed class AgentLoopPermissionModeTests
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

    private static AgentLoop BuildAgent(
        StubHandler handler, ToolRegistry registry, PermissionMode mode, IInteractivePrompter prompter)
    {
        var client = new LiteLLMClient(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "glm-5.2:cloud", MaxTurns = 5 },
            planMode: new PermissionModeState(mode), prompter: prompter);
    }

    [Fact]
    public async Task AcceptEdits_auto_allows_edit_tools_without_prompting()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Edit", "{\"file_path\":\"x.cs\"}")), Sse(FinalRound("done")));
        var registry = new ToolRegistry();
        var edit = new SpecTool("Edit", "file_path");
        registry.Register(edit);

        var prompter = new FakePrompter("Yes");
        var agent = BuildAgent(handler, registry, PermissionMode.AcceptEdits, prompter);
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        edit.Invocations.Should().Be(1);
        prompter.Calls.Should().Be(0); // auto-allowed — no prompt
    }

    [Fact]
    public async Task AcceptEdits_still_prompts_for_bash()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{\"command\":\"ls\"}")), Sse(FinalRound("done")));
        var registry = new ToolRegistry();
        var bash = new SpecTool("Bash", "command");
        registry.Register(bash);

        var prompter = new FakePrompter("Yes");
        var agent = BuildAgent(handler, registry, PermissionMode.AcceptEdits, prompter);
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        prompter.Calls.Should().Be(1); // Bash is not an edit tool → still asks
        bash.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Bypass_auto_allows_a_safe_bash_command()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{\"command\":\"ls -la\"}")), Sse(FinalRound("done")));
        var registry = new ToolRegistry();
        var bash = new SpecTool("Bash", "command");
        registry.Register(bash);

        var prompter = new FakePrompter("Yes");
        var agent = BuildAgent(handler, registry, PermissionMode.Bypass, prompter);
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        prompter.Calls.Should().Be(0); // bypass → no prompt
        bash.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Deny_floor_forces_a_prompt_for_a_dangerous_command_even_under_bypass()
    {
        var handler = new StubHandler(Sse(ToolCallRound("Bash", "{\"command\":\"rm -rf /\"}")), Sse(FinalRound("stopped")));
        var registry = new ToolRegistry();
        var bash = new SpecTool("Bash", "command");
        registry.Register(bash);

        // User declines the dangerous op at the forced prompt.
        var prompter = new FakePrompter("No, tell the model");
        var agent = BuildAgent(handler, registry, PermissionMode.Bypass, prompter);
        await agent.RunOneShotAsync("go", new StringWriter(), new StringWriter());

        prompter.Calls.Should().Be(1);       // deny-floor forced a confirm despite bypass
        bash.Invocations.Should().Be(0);     // declined → not executed
    }

    private sealed class FakePrompter : IInteractivePrompter
    {
        private readonly string _choice;
        public int Calls { get; private set; }
        public FakePrompter(string choice) => _choice = choice;
        public bool IsAvailable => true;
        public Task<IReadOnlyList<string>> SelectAsync(
            string q, string? h, IReadOnlyList<PromptChoice> o, bool ms, bool ft, CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<string>>(new[] { _choice }); }
    }

    // A tool that surfaces one of its args as the permission specifier (so Bash's command reaches
    // the dangerous-op detector), and counts executions.
    private sealed class SpecTool : ITool
    {
        private readonly string _name;
        private readonly string _specArg;
        public int Invocations { get; private set; }
        public SpecTool(string name, string specArg) { _name = name; _specArg = specArg; }

        public ToolSchema Schema => new(_name, $"{_name} tool.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public string? GetSpecifierForPermissions(JsonElement args) =>
            args.TryGetProperty(_specArg, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(ToolResult.Success("ran"));
        }
    }
}
