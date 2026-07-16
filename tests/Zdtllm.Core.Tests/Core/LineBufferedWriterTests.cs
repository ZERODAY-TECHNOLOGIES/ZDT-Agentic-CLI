using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class LineBufferedWriterTests
{
    [Fact]
    public void Invokes_callback_once_per_line()
    {
        var lines = new List<string>();
        using (var w = new LineBufferedWriter(lines.Add))
        {
            w.WriteLine("one");
            w.Write("two");
            w.Write("three\n");
        }
        lines.Should().Equal("one", "twothree");
    }

    [Fact]
    public void Drops_blank_lines_and_strips_cr()
    {
        var lines = new List<string>();
        using var w = new LineBufferedWriter(lines.Add);
        w.Write("a\r\n\r\nb\r\n");

        lines.Should().Equal("a", "b");
    }

    [Fact]
    public void Flushes_trailing_partial_line_on_dispose()
    {
        var lines = new List<string>();
        var w = new LineBufferedWriter(lines.Add);
        w.Write("partial");
        w.Dispose();

        lines.Should().Equal("partial");
    }
}
