using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class TodoWriteToolTests
{
    private static async Task<ToolResult> WriteAsync(TodoWriteTool tool, object todos)
    {
        var argsJson = JsonSerializer.Serialize(new { todos });
        using var doc = JsonDocument.Parse(argsJson);
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public async Task Sets_todo_list_from_payload()
    {
        var tool = new TodoWriteTool();

        var result = await WriteAsync(tool, new[]
        {
            new { id = "1", content = "Write tests", status = "in_progress" },
            new { id = "2", content = "Ship it", status = "pending" },
        });

        result.IsError.Should().BeFalse();
        tool.CurrentTodos.Should().HaveCount(2);
        tool.CurrentTodos[0].Content.Should().Be("Write tests");
        tool.CurrentTodos[0].Status.Should().Be("in_progress");
        result.Content.Should().Contain("Updated 2 todo(s)");
    }

    [Fact]
    public async Task Replaces_existing_list_on_subsequent_calls()
    {
        var tool = new TodoWriteTool();
        await WriteAsync(tool, new[] { new { id = "1", content = "first", status = "pending" } });

        await WriteAsync(tool, new[] { new { id = "2", content = "second", status = "completed" } });

        tool.CurrentTodos.Should().ContainSingle();
        tool.CurrentTodos[0].Content.Should().Be("second");
    }

    [Fact]
    public async Task Empty_array_clears_the_list()
    {
        var tool = new TodoWriteTool();
        await WriteAsync(tool, new[] { new { id = "1", content = "x", status = "pending" } });

        var result = await WriteAsync(tool, Array.Empty<object>());

        result.IsError.Should().BeFalse();
        tool.CurrentTodos.Should().BeEmpty();
        result.Content.Should().Contain("cleared");
    }

    [Fact]
    public async Task Rejects_invalid_status_value()
    {
        var tool = new TodoWriteTool();

        var result = await WriteAsync(tool, new[]
        {
            new { id = "1", content = "x", status = "wat" },
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("invalid status");
    }

    [Fact]
    public async Task Rejects_todo_missing_content()
    {
        var tool = new TodoWriteTool();

        var argsJson = """{"todos":[{"id":"1","status":"pending"}]}""";
        using var doc = JsonDocument.Parse(argsJson);
        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("content");
    }

    [Fact]
    public async Task Output_summary_includes_status_breakdown_and_checkbox_list()
    {
        var tool = new TodoWriteTool();
        var result = await WriteAsync(tool, new[]
        {
            new { id = "1", content = "Done thing", status = "completed" },
            new { id = "2", content = "Doing thing", status = "in_progress" },
            new { id = "3", content = "Pending thing", status = "pending" },
        });

        result.Content.Should().Contain("1 pending, 1 in progress, 1 completed");
        result.Content.Should().Contain("[x] Done thing");
        result.Content.Should().Contain("[~] Doing thing");
        result.Content.Should().Contain("[ ] Pending thing");
    }

    [Fact]
    public void Specifier_for_permissions_is_null()
    {
        var tool = new TodoWriteTool();
        using var doc = JsonDocument.Parse("""{"todos":[]}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().BeNull();
    }
}
