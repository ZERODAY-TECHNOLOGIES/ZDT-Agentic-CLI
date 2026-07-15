using Zdtllm.Cli.Input;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// Pure-logic tests for the line editor buffer that backs the interactive input (multi-line paste,
/// drag & drop, in-line editing). No console involved.
/// </summary>
public sealed class LineEditorStateTests
{
    [Fact]
    public void Typing_builds_up_the_line()
    {
        var s = new LineEditorState();
        s.InsertText("hello");

        s.Resolve().Should().Be("hello");
        s.Display().Should().Be("hello");
        s.Cursor.Should().Be(5);
        s.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Cursor_move_and_insert_in_the_middle()
    {
        var s = new LineEditorState();
        s.InsertText("helo");
        s.MoveLeft(); // between 'l' and 'o'
        s.InsertChar('l'); // -> "hello"

        s.Resolve().Should().Be("hello");
    }

    [Fact]
    public void Backspace_and_delete()
    {
        var s = new LineEditorState();
        s.InsertText("abcd");
        s.Backspace();          // "abc"
        s.Home();
        s.Delete();             // "bc"

        s.Resolve().Should().Be("bc");
    }

    [Fact]
    public void Home_end_kill_to_start_and_end()
    {
        var s = new LineEditorState();
        s.InsertText("hello world");

        s.Home();
        s.KillToEnd();
        s.Resolve().Should().BeEmpty();

        s.InsertText("hello world");
        s.End();
        s.KillToStart();
        s.Resolve().Should().BeEmpty();
    }

    [Fact]
    public void Pasted_block_renders_as_a_chip_but_resolves_to_full_text()
    {
        var s = new LineEditorState();
        s.InsertText("see: ");
        s.InsertPaste("line1\nline2\nline3");

        s.Display().Should().Be("see: [pasted 3 lines]");
        s.Resolve().Should().Be("see: line1\nline2\nline3");
    }

    [Fact]
    public void Cursor_column_accounts_for_chip_width()
    {
        var s = new LineEditorState();
        s.InsertPaste("a\nb"); // chip "[pasted 2 lines]" = 16 display chars

        s.CursorDisplayColumn.Should().Be("[pasted 2 lines]".Length);
    }

    [Fact]
    public void Backspacing_removes_a_whole_paste_chip_atomically()
    {
        var s = new LineEditorState();
        s.InsertText("x");
        s.InsertPaste("a\nb\nc");
        s.Backspace(); // removes the whole chip, not one char of it

        s.Resolve().Should().Be("x");
    }

    [Fact]
    public void Inline_newline_shows_a_marker_but_resolves_to_a_real_newline()
    {
        var s = new LineEditorState();
        s.InsertChar('a');
        s.InsertChar('\n');
        s.InsertChar('b');

        s.Display().Should().Be("a⏎b");
        s.Resolve().Should().Be("a\nb");
    }
}
