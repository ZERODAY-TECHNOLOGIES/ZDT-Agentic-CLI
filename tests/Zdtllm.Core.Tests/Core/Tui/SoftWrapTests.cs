using Zdtllm.Core.Tui;

namespace Zdtllm.Core.Tests.Core.Tui;

public sealed class SoftWrapTests
{
    private static (IReadOnlyList<VisualRow> Rows, int CursorIndex, int CursorCol) Layout(
        int width, int cursorRow, int cursorCol, params string[] lines) =>
        SoftWrap.Layout(lines, width, cursorRow, cursorCol);

    [Fact]
    public void Short_line_is_one_visual_row()
    {
        var (rows, ci, cc) = Layout(10, 0, 5, "hello");

        rows.Should().ContainSingle();
        rows[0].Text.Should().Be("hello");
        rows[0].LogicalRow.Should().Be(0);
        ci.Should().Be(0);
        cc.Should().Be(5);
    }

    [Fact]
    public void Line_exactly_the_width_stays_one_row_and_cursor_sits_at_the_edge()
    {
        var (rows, ci, cc) = Layout(10, 0, 10, "abcdefghij"); // len == width, cursor at end

        rows.Should().ContainSingle();
        rows[0].Text.Should().Be("abcdefghij");
        ci.Should().Be(0);
        cc.Should().Be(10); // "just past the last char" — no phantom empty row
    }

    [Fact]
    public void Long_line_wraps_onto_continuation_rows()
    {
        var (rows, ci, cc) = Layout(10, 0, 11, "abcdefghijk"); // len 11 → 2 rows

        rows.Should().HaveCount(2);
        rows[0].Text.Should().Be("abcdefghij");
        rows[0].StartCol.Should().Be(0);
        rows[1].Text.Should().Be("k");
        rows[1].StartCol.Should().Be(10);
        // cursor at col 11 → second row, column 1
        ci.Should().Be(1);
        cc.Should().Be(1);
    }

    [Fact]
    public void Cursor_in_the_first_segment_of_a_wrapped_line_stays_on_row_zero()
    {
        var (_, ci, cc) = Layout(10, 0, 3, "abcdefghijk");
        ci.Should().Be(0);
        cc.Should().Be(3);
    }

    [Fact]
    public void Multiple_logical_lines_each_wrap_independently()
    {
        // line 0 "aaa" (1 row); line 1 12 chars at width 5 → 3 rows.
        var (rows, ci, cc) = Layout(5, 1, 12, "aaa", "bbbbbbbbbbbb");

        rows.Should().HaveCount(4);
        rows[0].Text.Should().Be("aaa");
        rows[0].LogicalRow.Should().Be(0);
        rows[1].Text.Should().Be("bbbbb");
        rows[2].Text.Should().Be("bbbbb");
        rows[3].Text.Should().Be("bb");
        rows[3].LogicalRow.Should().Be(1);
        // cursor at (1,12) → last visual row, column 2
        ci.Should().Be(3);
        cc.Should().Be(2);
    }

    [Fact]
    public void Empty_line_occupies_one_row()
    {
        var (rows, ci, cc) = Layout(10, 0, 0, "");
        rows.Should().ContainSingle();
        rows[0].Text.Should().Be("");
        ci.Should().Be(0);
        cc.Should().Be(0);
    }

    [Fact]
    public void An_empty_line_between_content_is_preserved()
    {
        var (rows, _, _) = Layout(10, 0, 0, "", "x");
        rows.Should().HaveCount(2);
        rows[0].Text.Should().Be("");
        rows[1].Text.Should().Be("x");
    }

    [Fact]
    public void Zero_width_is_floored_to_one_and_does_not_throw()
    {
        var (rows, _, _) = Layout(0, 0, 0, "ab");
        rows.Should().HaveCount(2); // width 1 → two single-char rows
        rows[0].Text.Should().Be("a");
        rows[1].Text.Should().Be("b");
    }
}
