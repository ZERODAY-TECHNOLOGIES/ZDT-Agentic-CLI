using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class AtMentionExpanderTests : IDisposable
{
    private readonly string _dir;

    public AtMentionExpanderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zdt-at-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Inlines_a_referenced_file()
    {
        Write("notes.txt", "the secret is 42");

        var result = AtMentionExpander.Expand("check @notes.txt please", _dir);

        result.Should().Contain("check @notes.txt please"); // original prompt kept
        result.Should().Contain("the secret is 42");         // content inlined
        result.Should().Contain("notes.txt");
    }

    [Fact]
    public void Does_not_expand_an_email_address()
    {
        var prompt = "email me at office@zer0day.ro when done";
        AtMentionExpander.Expand(prompt, _dir).Should().Be(prompt);
    }

    [Fact]
    public void Leaves_unresolved_mentions_untouched()
    {
        var prompt = "look at @does/not/exist.cs";
        AtMentionExpander.Expand(prompt, _dir).Should().Be(prompt);
    }

    [Fact]
    public void Lists_a_referenced_directory()
    {
        Write("src/a.cs", "x");
        Write("src/b.cs", "y");

        var result = AtMentionExpander.Expand("what is in @src", _dir);

        result.Should().Contain("directory listing");
        result.Should().Contain("a.cs");
        result.Should().Contain("b.cs");
    }

    [Fact]
    public void Deduplicates_repeated_mentions()
    {
        Write("f.txt", "CONTENT-MARKER");

        var result = AtMentionExpander.Expand("@f.txt and again @f.txt", _dir);

        // Content inlined exactly once despite two mentions.
        result.Split("CONTENT-MARKER").Length.Should().Be(2);
    }

    [Fact]
    public void Does_not_inline_a_binary_file()
    {
        File.WriteAllBytes(Path.Combine(_dir, "blob.bin"), new byte[] { 1, 2, 0, 3, 4 });

        var result = AtMentionExpander.Expand("read @blob.bin", _dir);

        result.Should().Contain("binary file");
    }

    [Fact]
    public void No_at_sign_is_a_passthrough()
    {
        AtMentionExpander.Expand("just a normal prompt", _dir).Should().Be("just a normal prompt");
    }
}
