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

    private async Task<ToolResult> ReadAsync(string filePath, int? offset = null, int? limit = null)
    {
        var tool = new ReadTool();
        var argsObj = new Dictionary<string, object> { ["file_path"] = filePath };
        if (offset is int o) argsObj["offset"] = o;
        if (limit is int l) argsObj["limit"] = l;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(argsObj));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);
    }

    private async Task<ToolResult> ReadWithLegacyPathAsync(string path)
    {
        var tool = new ReadTool();
        var argsObj = new Dictionary<string, object> { ["path"] = path };
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
    public void Specifier_for_permissions_prefers_file_path()
    {
        var tool = new ReadTool();
        using var doc = JsonDocument.Parse("""{"file_path":"./.env"}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("./.env");
    }

    [Fact]
    public void Specifier_for_permissions_falls_back_to_legacy_path_alias()
    {
        var tool = new ReadTool();
        using var doc = JsonDocument.Parse("""{"path":"./legacy.txt"}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("./legacy.txt");
    }

    [Fact]
    public async Task Returns_error_when_file_path_arg_missing()
    {
        var tool = new ReadTool();
        using var doc = JsonDocument.Parse("{}");
        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("file_path");
    }

    [Fact]
    public async Task Legacy_path_alias_still_resolves_to_the_same_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "x.txt"), "alpha\nbeta\n");

        var fromLegacy = await ReadWithLegacyPathAsync("x.txt");
        var fromCanonical = await ReadAsync("x.txt");

        fromLegacy.IsError.Should().BeFalse();
        fromCanonical.IsError.Should().BeFalse();
        fromLegacy.Content.Should().Be(fromCanonical.Content);
    }

    [Fact]
    public async Task Refuses_files_above_size_cap_with_diagnostic_message()
    {
        // Defense-in-depth: even if perms allow it, ReadTool must refuse multi-MB files
        // before File.ReadAllLinesAsync materialises the whole content as string[]. The
        // cap is 5 MiB; we write 6 MiB of safe ASCII to make sure the gate fires on size
        // alone, not on any binary-content heuristic (which the tool intentionally doesn't have).
        var bigPath = Path.Combine(_tempDir, "big.txt");
        const int sixMib = 6 * 1024 * 1024;
        await File.WriteAllBytesAsync(bigPath, Enumerable.Repeat((byte)'A', sixMib).ToArray());

        var result = await ReadAsync("big.txt");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("too large");
        result.Content.Should().Contain("KiB");
        // Hint pointing at Glob is the actionable next step — assert it's there so a
        // future refactor doesn't accidentally drop it.
        result.Content.Should().Contain("Glob");
    }

    [Fact]
    public async Task Caps_output_and_paginates_a_large_multiline_file()
    {
        // 3000 lines of 100 chars ≈ 300 KB — well over the ~100 KB per-call budget, so the read is
        // capped mid-file and told where to continue (line-offset pagination, not a hard truncation).
        var content = string.Join('\n', Enumerable.Repeat(new string('x', 100), 3000)) + "\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "big.txt"), content);

        var result = await ReadAsync("big.txt");

        result.IsError.Should().BeFalse();
        result.Content.Length.Should().BeLessThan(110_000);      // capped near the ~100 KB budget
        result.Content.Should().Contain("     1\t");             // starts from line 1
        result.Content.Should().Contain("output capped");
        result.Content.Should().MatchRegex(@"Continue with offset: \d+");
    }

    [Fact]
    public async Task Continuation_offset_reads_the_next_page()
    {
        var content = string.Join('\n', Enumerable.Repeat(new string('x', 100), 3000)) + "\n";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "big.txt"), content);

        var first = await ReadAsync("big.txt");
        var m = System.Text.RegularExpressions.Regex.Match(first.Content, @"Continue with offset: (\d+)");
        m.Success.Should().BeTrue();
        var next = int.Parse(m.Groups[1].Value);

        var second = await ReadAsync("big.txt", offset: next);

        second.IsError.Should().BeFalse();
        second.Content.Should().Contain($"{next}\t");            // the next page begins exactly where we left off
        first.Content.Should().NotContain($"\n{next}\t");        // ...and the first page did NOT already include it
    }

    [Fact]
    public async Task Single_huge_line_shows_only_the_head_and_guides_to_byte_slicing()
    {
        // The exact shape that blew the context window: a big minified/JSON blob on ONE line. Line limits
        // don't bound it, so the byte budget must — show the head, then point at Grep / Bash slicing.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "blob.json"), new string('J', 300_000));

        var result = await ReadAsync("blob.json");

        result.IsError.Should().BeFalse();
        result.Content.Length.Should().BeLessThan(110_000);      // only the head, not all 300 KB
        result.Content.Should().Contain("very large");
        result.Content.Should().Contain("cut -c");               // Bash byte-slice guidance for the rest
    }

    [Fact]
    public async Task File_path_takes_precedence_when_both_aliases_provided()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "win.txt"), "winner\n");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "lose.txt"), "loser\n");

        var tool = new ReadTool();
        using var doc = JsonDocument.Parse("""{"file_path":"win.txt","path":"lose.txt"}""");
        var result = await tool.ExecuteAsync(doc.RootElement, new ToolContext(_tempDir), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("winner");
        result.Content.Should().NotContain("loser");
    }
}
