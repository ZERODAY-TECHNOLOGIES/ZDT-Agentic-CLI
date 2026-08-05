using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class TaskToolTests
{
    private static async Task<ToolResult> InvokeAsync(TaskTool tool, object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public void Schema_advertises_tool_name_as_Agent_for_claude_cli_compat()
    {
        // Anchored test: the model's tool catalog must call this tool "Agent" so
        // AppSec-Automator's `--tools Read Glob Grep Agent` reaches us via the existing
        // ArgumentParser → ApplyToolsAllowlist path. Renaming back to "Task" would silently
        // break drop-in compat without anything else flagging it.
        var tool = new TaskTool(new RecordingRunner(new SubagentResult("ok", 0, null, null)));
        tool.Schema.Name.Should().Be("Agent");
        TaskTool.ToolName.Should().Be("Agent");
    }

    [Fact]
    public async Task Forwards_request_to_runner_and_wraps_metadata_around_output()
    {
        var runner = new RecordingRunner(new SubagentResult("the answer", Turns: 3, PromptTokens: 421, CompletionTokens: 88));
        var tool = new TaskTool(runner);

        var result = await InvokeAsync(tool, new
        {
            description = "audit pastebin",
            prompt = "Review view.php and index.php for XSS",
            subagent_type = "code-reviewer",
        });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("[subagent code-reviewer");
        result.Content.Should().Contain("3 turn(s)");
        result.Content.Should().Contain("421 prompt tokens");
        result.Content.Should().Contain("the answer");

        runner.LastRequest.Should().NotBeNull();
        runner.LastRequest!.Description.Should().Be("audit pastebin");
        runner.LastRequest.Prompt.Should().Be("Review view.php and index.php for XSS");
        runner.LastRequest.Type.Should().Be("code-reviewer");
    }

    [Fact]
    public async Task Default_subagent_type_is_general_purpose()
    {
        var runner = new RecordingRunner(new SubagentResult("ok", 1, null, null));
        var tool = new TaskTool(runner);

        await InvokeAsync(tool, new { description = "x", prompt = "y" });

        runner.LastRequest!.Type.Should().Be("general-purpose");
    }

    [Fact]
    public async Task Returns_error_when_description_missing()
    {
        var tool = new TaskTool(new RecordingRunner(new SubagentResult("never", 0, null, null)));

        var result = await InvokeAsync(tool, new { prompt = "y" });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("description");
    }

    [Fact]
    public async Task Returns_error_when_prompt_missing()
    {
        var tool = new TaskTool(new RecordingRunner(new SubagentResult("never", 0, null, null)));

        var result = await InvokeAsync(tool, new { description = "x" });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("prompt");
    }

    [Fact]
    public async Task Rejects_unknown_subagent_type_with_available_list()
    {
        var runner = new RecordingRunner(new SubagentResult("never", 0, null, null))
        {
            Available = new[] { "general-purpose", "code-reviewer" },
        };
        var tool = new TaskTool(runner);

        var result = await InvokeAsync(tool, new
        {
            description = "x",
            prompt = "y",
            subagent_type = "nope",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("nope");
        result.Content.Should().Contain("code-reviewer");
    }

    [Fact]
    public async Task Wraps_runner_exceptions_in_error_result()
    {
        var runner = new ThrowingRunner();
        var tool = new TaskTool(runner);

        var result = await InvokeAsync(tool, new
        {
            description = "x",
            prompt = "y",
            subagent_type = "general-purpose",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("subagent failed");
        result.Content.Should().Contain("synthetic boom");
    }

    [Fact]
    public async Task ToolContext_Model_is_forwarded_as_SubagentRequest_ParentModel()
    {
        // /model in the REPL mutates session.Model, AgentLoop populates ctx.Model from
        // session.Model on each turn, and TaskTool must thread ctx.Model into the
        // SubagentRequest so the runner uses the CURRENT model — not the parent
        // AgentLoop's startup-frozen one. This test pins that wiring.
        var runner = new RecordingRunner(new SubagentResult("ok", 1, null, null));
        var tool = new TaskTool(runner);

        using var doc = JsonDocument.Parse("""{"description":"d","prompt":"p"}""");
        await tool.ExecuteAsync(
            doc.RootElement,
            new ToolContext(Cwd: Path.GetTempPath(), Model: "qwen-medium"),
            CancellationToken.None);

        runner.LastRequest!.ParentModel.Should().Be("qwen-medium");
    }

    [Fact]
    public async Task Null_ToolContext_Model_yields_null_ParentModel_letting_runner_fall_back()
    {
        // Backwards-compat path: a caller that doesn't set ctx.Model (older test fixtures,
        // tools constructing ToolContext without piping a session in) shouldn't fabricate
        // a model name — the runner should observe null and fall back to its own option.
        var runner = new RecordingRunner(new SubagentResult("ok", 1, null, null));
        var tool = new TaskTool(runner);

        using var doc = JsonDocument.Parse("""{"description":"d","prompt":"p"}""");
        await tool.ExecuteAsync(
            doc.RootElement,
            new ToolContext(Cwd: Path.GetTempPath()), // Model defaults to null
            CancellationToken.None);

        runner.LastRequest!.ParentModel.Should().BeNull();
    }

    [Fact]
    public void Specifier_for_permissions_is_the_subagent_type()
    {
        var tool = new TaskTool(new RecordingRunner(new SubagentResult("x", 0, null, null)));
        using var doc = JsonDocument.Parse("""{"description":"d","prompt":"p","subagent_type":"explore"}""");

        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("explore");
    }

    [Fact]
    public async Task Blocked_status_is_surfaced_in_the_header_with_a_do_not_redispatch_hint()
    {
        var runner = new RecordingRunner(
            new SubagentResult("could not find the DB creds", Turns: 4, PromptTokens: null,
                CompletionTokens: null, Model: null, Status: "blocked"));
        var tool = new TaskTool(runner);

        var result = await InvokeAsync(tool, new { description = "x", prompt = "y", subagent_type = "general-purpose" });

        result.Content.Should().Contain("STATUS: blocked");
        result.Content.Should().Contain("did NOT complete");     // the orchestrator hint
        result.Content.Should().Contain("could not find the DB creds"); // report body preserved
    }

    [Fact]
    public async Task Completed_status_shows_no_redispatch_hint()
    {
        var runner = new RecordingRunner(
            new SubagentResult("all done", Turns: 2, PromptTokens: null, CompletionTokens: null,
                Model: null, Status: "completed"));
        var tool = new TaskTool(runner);

        var result = await InvokeAsync(tool, new { description = "x", prompt = "y" });

        result.Content.Should().Contain("STATUS: completed");
        result.Content.Should().NotContain("did NOT complete");
    }

    private sealed class RecordingRunner : ISubagentRunner
    {
        private readonly SubagentResult _result;
        public SubagentRequest? LastRequest { get; private set; }
        public string[] Available { get; init; } = new[] { "general-purpose", "code-reviewer", "explore" };

        public RecordingRunner(SubagentResult result) { _result = result; }

        public IReadOnlyList<string> AvailableTypes => Available;
        public bool SupportsType(string type) => Available.Contains(type, StringComparer.Ordinal);
        public IReadOnlyList<SubagentTypeInfo> GetTypeInfo() =>
            Available.Select(t => new SubagentTypeInfo(t, "stub", new[] { "*" })).ToList();

        public Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingRunner : ISubagentRunner
    {
        public IReadOnlyList<string> AvailableTypes { get; } = new[] { "general-purpose" };
        public bool SupportsType(string type) => true;
        public IReadOnlyList<SubagentTypeInfo> GetTypeInfo() =>
            new[] { new SubagentTypeInfo("general-purpose", "stub", new[] { "*" }) };
        public Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("synthetic boom");
    }
}
