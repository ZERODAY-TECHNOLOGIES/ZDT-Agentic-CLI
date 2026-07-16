using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class LivePrefixWriterTests
{
    [Fact]
    public void Prefixes_each_completed_line()
    {
        var sink = new StringWriter();
        using (var w = new LivePrefixWriter(sink, new object(), "[a] "))
        {
            w.WriteLine("first");
            w.WriteLine("second");
        }

        var lines = sink.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        lines.Should().Equal("[a] first", "[a] second");
    }

    [Fact]
    public void Buffers_partial_writes_until_a_newline()
    {
        var sink = new StringWriter();
        var w = new LivePrefixWriter(sink, new object(), "[x] ");
        w.Write("hel");
        w.Write("lo");
        sink.ToString().Should().BeEmpty(); // nothing flushed yet — no newline
        w.Write("\n");
        sink.ToString().Should().Contain("[x] hello");
    }

    [Fact]
    public void Drops_blank_lines()
    {
        var sink = new StringWriter();
        using var w = new LivePrefixWriter(sink, new object(), "[x] ");
        w.WriteLine();
        w.WriteLine("");
        w.WriteLine("real");

        sink.ToString().Should().Contain("[x] real");
        // No prefix emitted for the two blank lines.
        System.Text.RegularExpressions.Regex.Matches(sink.ToString(), @"\[x\] ").Count.Should().Be(1);
    }

    [Fact]
    public void Strips_carriage_returns()
    {
        var sink = new StringWriter();
        using var w = new LivePrefixWriter(sink, new object(), "[x] ");
        w.Write("a\r\nb\r\n");

        var lines = sink.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        lines.Should().Equal("[x] a", "[x] b");
    }

    [Fact]
    public void Flushes_trailing_partial_line_on_dispose()
    {
        var sink = new StringWriter();
        var w = new LivePrefixWriter(sink, new object(), "[x] ");
        w.Write("no newline");
        w.Dispose();

        sink.ToString().Should().Contain("[x] no newline");
    }

    [Fact]
    public void Two_writers_sharing_a_lock_never_interleave_a_line()
    {
        var sink = new StringWriter();
        var shared = new object();
        var a = new LivePrefixWriter(sink, shared, "[a] ");
        var b = new LivePrefixWriter(sink, shared, "[b] ");

        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0) a.WriteLine($"a{i}");
            else b.WriteLine($"b{i}");
        });
        a.Dispose();
        b.Dispose();

        // Every emitted line is intact: starts with a known prefix and has no embedded prefix.
        foreach (var line in sink.ToString().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            (line.StartsWith("[a] ") || line.StartsWith("[b] ")).Should().BeTrue($"line was '{line}'");
        }
    }
}
