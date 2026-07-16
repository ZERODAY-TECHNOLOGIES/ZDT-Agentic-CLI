using System.Text;

namespace Zdtllm.Core.Tui;

/// <summary>
/// The pure, console-free model behind the persistent multi-line input box: a list of text lines
/// plus a (row, col) cursor, with all the editing operations a Claude-Code-style input needs —
/// typing, newlines, paste, backspace/delete, and arrow/Home/End navigation across lines. No
/// rendering, no <c>Console</c>; the TUI shell draws <see cref="Lines"/> and positions the cursor
/// from <see cref="CursorRow"/>/<see cref="CursorCol"/>. Unit-testable in isolation.
/// </summary>
public sealed class MultiLineEditor
{
    private readonly List<StringBuilder> _lines = new() { new StringBuilder() };
    private int _row;
    private int _col;

    public int CursorRow => _row;
    public int CursorCol => _col;
    public int LineCount => _lines.Count;
    public bool IsEmpty => _lines.Count == 1 && _lines[0].Length == 0;

    /// <summary>Snapshot of the current lines (for rendering).</summary>
    public IReadOnlyList<string> Lines => _lines.Select(l => l.ToString()).ToList();

    /// <summary>The full text, lines joined with '\n'.</summary>
    public string Text => string.Join("\n", _lines.Select(l => l.ToString()));

    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new StringBuilder());
        _row = 0;
        _col = 0;
    }

    public void InsertChar(char c)
    {
        if (c == '\n') { Newline(); return; }
        if (char.IsControl(c)) return;
        _lines[_row].Insert(_col, c);
        _col++;
    }

    /// <summary>Insert text, splitting on newlines into multiple lines (used by paste).</summary>
    public void InsertText(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        foreach (var c in s.Replace("\r\n", "\n").Replace('\r', '\n'))
            InsertChar(c);
    }

    public void Newline()
    {
        var cur = _lines[_row];
        var tail = cur.ToString(_col, cur.Length - _col);
        cur.Length = _col;                       // truncate current line at cursor
        _lines.Insert(_row + 1, new StringBuilder(tail));
        _row++;
        _col = 0;
    }

    public void Backspace()
    {
        if (_col > 0)
        {
            _lines[_row].Remove(_col - 1, 1);
            _col--;
        }
        else if (_row > 0)
        {
            // Merge this line into the previous one.
            var prev = _lines[_row - 1];
            var merged = _lines[_row].ToString();
            _col = prev.Length;
            prev.Append(merged);
            _lines.RemoveAt(_row);
            _row--;
        }
    }

    public void Delete()
    {
        var line = _lines[_row];
        if (_col < line.Length)
        {
            line.Remove(_col, 1);
        }
        else if (_row < _lines.Count - 1)
        {
            // Pull the next line up onto this one.
            line.Append(_lines[_row + 1]);
            _lines.RemoveAt(_row + 1);
        }
    }

    public void Left()
    {
        if (_col > 0) _col--;
        else if (_row > 0) { _row--; _col = _lines[_row].Length; }
    }

    public void Right()
    {
        if (_col < _lines[_row].Length) _col++;
        else if (_row < _lines.Count - 1) { _row++; _col = 0; }
    }

    public void Up()
    {
        if (_row > 0) { _row--; _col = Math.Min(_col, _lines[_row].Length); }
    }

    public void Down()
    {
        if (_row < _lines.Count - 1) { _row++; _col = Math.Min(_col, _lines[_row].Length); }
    }

    public void Home() => _col = 0;

    public void End() => _col = _lines[_row].Length;

    /// <summary>Delete from the cursor to the end of the current line (Ctrl+K).</summary>
    public void KillToEnd() => _lines[_row].Length = _col;

    /// <summary>Delete from the start of the current line to the cursor (Ctrl+U).</summary>
    public void KillToStart()
    {
        _lines[_row].Remove(0, _col);
        _col = 0;
    }
}
