using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class EditToolTests : IDisposable
{
    private readonly string _tempDir;

    public EditToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<ToolResult> EditAsync(string path, string oldStr, string newStr, bool? replaceAll = null)
    {
        var tool = new EditTool();
        object argsObj = replaceAll is null
            ? new { file_path = path, old_string = oldStr, new_string = newStr }
            : (object)new { file_path = path, old_string = oldStr, new_string = newStr, replace_all = replaceAll.Value };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(argsObj));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);
    }

    private async Task<string> WriteFileAsync(string name, string contents)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    [Fact]
    public async Task Replaces_unique_occurrence_successfully()
    {
        var path = await WriteFileAsync("a.txt", "hello world");

        var result = await EditAsync("a.txt", "world", "there");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("replaced 1 occurrence");
        (await File.ReadAllTextAsync(path)).Should().Be("hello there");
    }

    [Fact]
    public async Task Errors_when_old_string_appears_multiple_times_without_replace_all()
    {
        await WriteFileAsync("a.txt", "foo bar foo baz");

        var result = await EditAsync("a.txt", "foo", "qux");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("occurs 2 times");
        result.Content.Should().Contain("replace_all");
    }

    [Fact]
    public async Task Replace_all_replaces_every_occurrence()
    {
        var path = await WriteFileAsync("a.txt", "foo bar foo baz foo");

        var result = await EditAsync("a.txt", "foo", "qux", replaceAll: true);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("replaced 3 occurrence");
        (await File.ReadAllTextAsync(path)).Should().Be("qux bar qux baz qux");
    }

    [Fact]
    public async Task Errors_when_old_string_not_found()
    {
        await WriteFileAsync("a.txt", "hello world");

        var result = await EditAsync("a.txt", "missing", "x");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Errors_when_file_does_not_exist()
    {
        var result = await EditAsync("nope.txt", "x", "y");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Errors_when_old_string_is_empty()
    {
        await WriteFileAsync("a.txt", "anything");

        var result = await EditAsync("a.txt", "", "x");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("empty");
    }

    [Fact]
    public async Task Errors_when_old_and_new_are_identical()
    {
        await WriteFileAsync("a.txt", "hello");

        var result = await EditAsync("a.txt", "hello", "hello");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("identical");
    }

    [Fact]
    public async Task Replace_all_passed_as_string_true_is_honored_for_xml_mode()
    {
        // XML-mode tool calls deliver booleans as strings; the tool should coerce.
        var path = await WriteFileAsync("a.txt", "x x x");

        var tool = new EditTool();
        var argsJson = JsonSerializer.Serialize(new { file_path = "a.txt", old_string = "x", new_string = "y", replace_all = "true" });
        using var doc = JsonDocument.Parse(argsJson);
        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(path)).Should().Be("y y y");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_file_path()
    {
        var tool = new EditTool();
        using var doc = JsonDocument.Parse("""{"file_path":"./.env","old_string":"x","new_string":"y"}""");

        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("./.env");
    }
}
