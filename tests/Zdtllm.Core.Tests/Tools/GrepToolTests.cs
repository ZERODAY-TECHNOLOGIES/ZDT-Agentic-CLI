using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class GrepToolTests : IDisposable
{
    private readonly string _tempDir;

    public GrepToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-grep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private void WriteFile(string rel, string content)
    {
        var full = Path.Combine(_tempDir, rel);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    private async Task<ToolResult> GrepAsync(Dictionary<string, object> args)
    {
        var tool = new GrepTool();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);
    }

    [Fact]
    public async Task Files_with_matches_lists_only_files_that_match()
    {
        WriteFile("a.txt", "hello world\nanother line\n");
        WriteFile("b.txt", "no match here\n");
        WriteFile("c.txt", "world of warcraft\n");

        var result = await GrepAsync(new() { ["pattern"] = "world" });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("a.txt");
        result.Content.Should().Contain("c.txt");
        result.Content.Should().NotContain("b.txt");
    }

    [Fact]
    public async Task Skips_the_zdtllm_state_dir()
    {
        WriteFile("src.txt", "needle here\n");
        WriteFile(".zdtllm/sessions/session.jsonl", "needle in the session file\n");

        var result = await GrepAsync(new() { ["pattern"] = "needle" });

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("src.txt");
        result.Content.Should().NotContain("session.jsonl"); // .zdtllm pruned — no grepping our own session
    }

    [Fact]
    public async Task Content_mode_prints_matched_lines()
    {
        WriteFile("a.txt", "hello world\nanother line\nworld again\n");

        var result = await GrepAsync(new() { ["pattern"] = "world", ["output_mode"] = "content" });

        result.Content.Should().Contain("hello world");
        result.Content.Should().Contain("world again");
        result.Content.Should().NotContain("another line");
    }

    [Fact]
    public async Task Content_mode_with_n_includes_line_numbers()
    {
        WriteFile("a.txt", "first\nsecond match\nthird\n");

        var result = await GrepAsync(new() { ["pattern"] = "match", ["output_mode"] = "content", ["-n"] = true });

        result.Content.Should().Contain(":2:");
    }

    [Fact]
    public async Task Count_mode_returns_per_file_counts()
    {
        WriteFile("a.txt", "x\nx\nx\n");
        WriteFile("b.txt", "x\n");

        var result = await GrepAsync(new() { ["pattern"] = "x", ["output_mode"] = "count" });

        result.Content.Should().Contain("a.txt:3");
        result.Content.Should().Contain("b.txt:1");
    }

    [Fact]
    public async Task Case_insensitive_flag_matches_regardless_of_case()
    {
        WriteFile("a.txt", "Hello World\n");

        var resultCaseSensitive = await GrepAsync(new() { ["pattern"] = "world" });
        resultCaseSensitive.Content.Should().Be("(no matches)");

        var resultCaseInsensitive = await GrepAsync(new() { ["pattern"] = "world", ["-i"] = true });
        resultCaseInsensitive.Content.Should().Contain("a.txt");
    }

    [Fact]
    public async Task Glob_filter_restricts_files_scanned()
    {
        WriteFile("a.cs", "needle\n");
        WriteFile("b.txt", "needle\n");

        var result = await GrepAsync(new() { ["pattern"] = "needle", ["glob"] = "*.cs" });

        result.Content.Should().Contain("a.cs");
        result.Content.Should().NotContain("b.txt");
    }

    [Fact]
    public async Task Invalid_regex_returns_error()
    {
        var result = await GrepAsync(new() { ["pattern"] = "(unclosed" });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("invalid regex");
    }

    [Fact]
    public async Task No_matches_returns_no_matches_message()
    {
        WriteFile("a.txt", "nothing\n");

        var result = await GrepAsync(new() { ["pattern"] = "needle" });

        result.Content.Should().Contain("(no matches)");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_pattern()
    {
        var tool = new GrepTool();
        using var doc = JsonDocument.Parse("""{"pattern":"TODO"}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("TODO");
    }

    [Fact]
    public async Task Skips_build_and_vcs_directories()
    {
        WriteFile("src/a.cs", "needle here\n");
        WriteFile("bin/generated.cs", "needle here\n");
        WriteFile("obj/Debug/gen.cs", "needle here\n");
        WriteFile(".git/config", "needle here\n");
        WriteFile("node_modules/pkg/index.js", "needle here\n");

        var result = await GrepAsync(new() { ["pattern"] = "needle" });

        result.Content.Should().Contain(Path.Combine("src", "a.cs"));
        result.Content.Should().NotContain("bin");
        result.Content.Should().NotContain("obj");
        result.Content.Should().NotContain(".git");
        result.Content.Should().NotContain("node_modules");
    }

    [Fact]
    public async Task Skips_binary_files()
    {
        WriteFile("readme.txt", "plain needle text\n");
        File.WriteAllBytes(Path.Combine(_tempDir, "blob.bin"), new byte[] { 0x6E, 0x00, 0x65, 0x65 }); // has NUL

        var result = await GrepAsync(new() { ["pattern"] = "needle|nee" });

        result.Content.Should().Contain("readme.txt");
        result.Content.Should().NotContain("blob.bin");
    }

    [Fact]
    public async Task Context_C_includes_surrounding_lines()
    {
        WriteFile("a.txt", "line1\nline2\nMATCH\nline4\nline5\n");

        var result = await GrepAsync(new() { ["pattern"] = "MATCH", ["output_mode"] = "content", ["-C"] = 1 });

        result.Content.Should().Contain("line2");
        result.Content.Should().Contain("MATCH");
        result.Content.Should().Contain("line4");
        result.Content.Should().NotContain("line1");
        result.Content.Should().NotContain("line5");
    }

    [Fact]
    public async Task Type_alias_restricts_files_scanned()
    {
        WriteFile("a.cs", "needle\n");
        WriteFile("b.py", "needle\n");

        var result = await GrepAsync(new() { ["pattern"] = "needle", ["type"] = "cs" });

        result.Content.Should().Contain("a.cs");
        result.Content.Should().NotContain("b.py");
    }

    [Fact]
    public async Task Multiline_matches_across_line_boundaries()
    {
        WriteFile("a.txt", "start\nfoo\nbar\nend\n");

        // Without multiline this pattern can't match (spans lines); with it, it does.
        var flat = await GrepAsync(new() { ["pattern"] = "foo.bar" });
        flat.Content.Should().Be("(no matches)");

        var ml = await GrepAsync(new() { ["pattern"] = "foo.bar", ["multiline"] = true });
        ml.Content.Should().Contain("a.txt");
    }
}
