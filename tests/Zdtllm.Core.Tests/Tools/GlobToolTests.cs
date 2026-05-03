using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class GlobToolTests : IDisposable
{
    private readonly string _tempDir;

    public GlobToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task<ToolResult> GlobAsync(string pattern, string? path = null)
    {
        var tool = new GlobTool();
        object argsObj = path is null
            ? new { pattern }
            : (object)new { pattern, path };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(argsObj));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);
    }

    private void Touch(string relPath, DateTime? lastWrite = null)
    {
        var full = Path.Combine(_tempDir, relPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, "");
        if (lastWrite is DateTime t) File.SetLastWriteTime(full, t);
    }

    [Fact]
    public async Task Top_level_star_pattern_finds_files_in_cwd()
    {
        Touch("a.txt");
        Touch("b.txt");
        Touch("nested/c.txt");

        var result = await GlobAsync("*.txt");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("a.txt");
        result.Content.Should().Contain("b.txt");
        result.Content.Should().NotContain("c.txt"); // top-level only
    }

    [Fact]
    public async Task Recursive_double_star_finds_files_in_subdirectories()
    {
        Touch("a.txt");
        Touch("nested/deep/b.txt");

        var result = await GlobAsync("**/*.txt");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("a.txt");
        result.Content.Should().Contain("b.txt");
    }

    [Fact]
    public async Task No_matches_returns_no_matches_message()
    {
        Touch("a.cs");

        var result = await GlobAsync("**/*.fsproj");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("(no matches)");
    }

    [Fact]
    public async Task Results_are_sorted_by_mtime_descending()
    {
        var older = DateTime.Now.AddHours(-2);
        var newer = DateTime.Now;
        Touch("older.txt", older);
        Touch("newer.txt", newer);

        var result = await GlobAsync("*.txt");

        // StringBuilder.AppendLine uses Environment.NewLine, so split + trim \r before filtering.
        var lines = result.Content
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.EndsWith(".txt"))
            .ToArray();
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("newer.txt");
        lines[1].Should().Contain("older.txt");
    }

    [Fact]
    public async Task Returns_error_for_missing_directory()
    {
        var result = await GlobAsync("*.cs", path: "/does/not/exist");

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void Specifier_for_permissions_is_the_pattern()
    {
        var tool = new GlobTool();
        using var doc = JsonDocument.Parse("""{"pattern":"**/*.cs"}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("**/*.cs");
    }
}
