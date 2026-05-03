using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class ReadToolTests : IDisposable
{
    private readonly string _tempDir;

    public ReadToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<ToolResult> ReadAsync(string path, int? offset = null, int? limit = null)
    {
        var tool = new ReadTool();
        var argsObj = new Dictionary<string, object> { ["path"] = path };
        if (offset is int o) argsObj["offset"] = o;
        if (limit is int l) argsObj["limit"] = l;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(argsObj));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);
    }

    [Fact]
    public async Task Reads_file_with_line_numbers_starting_at_1()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "x.txt"), "alpha\nbeta\ngamma\n");

        var result = await ReadAsync("x.txt");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("     1\talpha");
        result.Content.Should().Contain("     2\tbeta");
        result.Content.Should().Contain("     3\tgamma");
    }

    [Fact]
    public async Task Returns_error_for_missing_file()
    {
        var result = await ReadAsync("does-not-exist.txt");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Respects_offset_and_limit()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "x.txt"), "a\nb\nc\nd\ne\n");

        var result = await ReadAsync("x.txt", offset: 2, limit: 2);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("     2\tb");
        result.Content.Should().Contain("     3\tc");
        result.Content.Should().NotContain("     4\td");
        result.Content.Should().NotContain("     1\ta");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_path_arg()
    {
        var tool = new ReadTool();
        using var doc = JsonDocument.Parse("""{"path":"./.env"}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("./.env");
    }

    [Fact]
    public async Task Returns_error_when_path_arg_missing()
    {
        var tool = new ReadTool();
        using var doc = JsonDocument.Parse("{}");
        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("path");
    }
}
