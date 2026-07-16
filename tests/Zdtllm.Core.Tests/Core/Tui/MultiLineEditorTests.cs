using Zdtllm.Core.Tui;

namespace Zdtllm.Core.Tests.Core.Tui;

public sealed class MultiLineEditorTests
{
    [Fact]
    public void Typing_builds_one_line()
    {
        var e = new MultiLineEditor();
        foreach (var c in "hello") e.InsertChar(c);

        e.Text.Should().Be("hello");
        e.LineCount.Should().Be(1);
        e.CursorRow.Should().Be(0);
        e.CursorCol.Should().Be(5);
        e.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Newline_splits_the_line_at_the_cursor()
    {
        var e = new MultiLineEditor();
        e.InsertText("abcd");
        e.Left(); e.Left();       // between b and c
        e.Newline();

        e.Lines.Should().Equal("ab", "cd");
        e.CursorRow.Should().Be(1);
        e.CursorCol.Should().Be(0);
    }

    [Fact]
    public void Insert_text_with_newlines_creates_lines()
    {
        var e = new MultiLineEditor();
        e.InsertText("one\ntwo\r\nthree");

        e.Lines.Should().Equal("one", "two", "three");
        e.CursorRow.Should().Be(2);
        e.CursorCol.Should().Be(5);
    }

    [Fact]
    public void Backspace_at_line_start_merges_with_previous()
    {
        var e = new MultiLineEditor();
        e.InsertText("ab\ncd");   // cursor at end of "cd"
        e.Home();                 // start of "cd"
        e.Backspace();            // merge into "ab"

        e.Lines.Should().Equal("abcd");
        e.CursorRow.Should().Be(0);
        e.CursorCol.Should().Be(2);
    }

    [Fact]
    public void Delete_at_line_end_pulls_next_line_up()
    {
        var e = new MultiLineEditor();
        e.InsertText("ab\ncd");
        e.CursorRowTo(0); e.End();  // end of "ab"
        e.Delete();

        e.Lines.Should().Equal("abcd");
    }

    [Fact]
    public void Arrows_navigate_across_lines_clamping_column()
    {
        var e = new MultiLineEditor();
        e.InsertText("long line\nx");   // row1 "x" is short
        // cursor at end of "x" (row 1, col 1)
        e.Up();                          // to row 0; col clamps to <= line length
        e.CursorRow.Should().Be(0);
        e.CursorCol.Should().Be(1);      // clamped to min(1, len)
        e.Down();
        e.CursorRow.Should().Be(1);
    }

    [Fact]
    public void Home_end_and_kill_operations()
    {
        var e = new MultiLineEditor();
        e.InsertText("hello world");
        e.Home(); e.CursorCol.Should().Be(0);
        e.End();  e.CursorCol.Should().Be(11);

        e.Home(); e.KillToEnd();
        e.Text.Should().BeEmpty();

        e.InsertText("hello world");
        e.End(); e.KillToStart();
        e.Text.Should().BeEmpty();
    }

    [Fact]
    public void Clear_resets_to_empty()
    {
        var e = new MultiLineEditor();
        e.InsertText("a\nb\nc");
        e.Clear();
        e.IsEmpty.Should().BeTrue();
        e.LineCount.Should().Be(1);
        e.Text.Should().BeEmpty();
    }
}

internal static class MultiLineEditorTestExtensions
{
    // Helper: move the cursor to a given row (top) then let the test set the column via Home/End.
    public static void CursorRowTo(this MultiLineEditor e, int row)
    {
        while (e.CursorRow > row) e.Up();
        while (e.CursorRow < row) e.Down();
    }
}
