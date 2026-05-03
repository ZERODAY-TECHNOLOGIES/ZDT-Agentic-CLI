using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class WriteToolTests : IDisposable
{
    private readonly string _tempDir;

    public WriteToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<ToolResult> WriteAsync(string path, string content)
    {
        var tool = new WriteTool();
        var argsJson = JsonSerializer.Serialize(new { file_path = path, content });
        using var doc = JsonDocument.Parse(argsJson);
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);
    }

    [Fact]
    public async Task Writes_a_new_file()
    {
        var result = await WriteAsync("hello.txt", "world\n");

        result.IsError.Should().BeFalse();
        var path = Path.Combine(_tempDir, "hello.txt");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("world\n");
    }

    [Fact]
    public async Task Overwrites_existing_file()
    {
        var path = Path.Combine(_tempDir, "exists.txt");
        await File.WriteAllTextAsync(path, "OLD");

        var result = await WriteAsync("exists.txt", "NEW");

        result.IsError.Should().BeFalse();
        (await File.ReadAllTextAsync(path)).Should().Be("NEW");
    }

    [Fact]
    public async Task Creates_parent_directories_as_needed()
    {
        var result = await WriteAsync("nested/deep/file.txt", "content");

        result.IsError.Should().BeFalse();
        File.Exists(Path.Combine(_tempDir, "nested", "deep", "file.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Reports_byte_count_in_success_message()
    {
        var result = await WriteAsync("a.txt", "hello"); // 5 bytes UTF-8

        result.Content.Should().Contain("Wrote 5 bytes");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_file_path()
    {
        var tool = new WriteTool();
        using var doc = JsonDocument.Parse("""{"file_path":"./.env","content":"X"}""");

        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("./.env");
    }

    [Fact]
    public async Task Returns_error_when_file_path_missing()
    {
        var tool = new WriteTool();
        using var doc = JsonDocument.Parse("""{"content":"x"}""");

        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("file_path");
    }
}
