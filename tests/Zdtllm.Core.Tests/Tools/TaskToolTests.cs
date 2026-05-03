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
    public void Specifier_for_permissions_is_the_subagent_type()
    {
        var tool = new TaskTool(new RecordingRunner(new SubagentResult("x", 0, null, null)));
        using var doc = JsonDocument.Parse("""{"description":"d","prompt":"p","subagent_type":"explore"}""");

        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("explore");
    }

    private sealed class RecordingRunner : ISubagentRunner
    {
        private readonly SubagentResult _result;
        public SubagentRequest? LastRequest { get; private set; }
        public string[] Available { get; init; } = new[] { "general-purpose", "code-reviewer", "explore" };

        public RecordingRunner(SubagentResult result) { _result = result; }

        public IReadOnlyList<string> AvailableTypes => Available;
        public bool SupportsType(string type) => Available.Contains(type, StringComparer.Ordinal);

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
        public Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("synthetic boom");
    }
}
