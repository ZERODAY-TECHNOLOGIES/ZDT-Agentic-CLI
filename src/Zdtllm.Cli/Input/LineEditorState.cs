using System.Text;

namespace Zdtllm.Cli.Input;

/// <summary>
/// The pure, console-free model behind the interactive line editor: an ordered list of elements
/// (single characters, or collapsed "paste" blocks) plus a cursor. It knows two projections —
/// <see cref="Display"/> for what's drawn on the single terminal line, and <see cref="Resolve"/>
/// for the real multi-line text that gets submitted. A large or multi-line paste is stored as one
/// <c>PasteElement</c> so it renders as a compact <c>[pasted N lines]</c> chip instead of flooding
/// the line — matching claude-cli — while its full text still submits verbatim.
///
/// All editing logic lives here (no <c>Console</c> calls) so it's unit-testable; the console
/// driver (<see cref="ConsoleInput"/>) only renders this state and feeds it keystrokes.
/// </summary>
internal sealed class LineEditorState
{
    private abstract class Element
    {
        public abstract string Value { get; }   // real text (for submit)
        public abstract string Display { get; }  // what's shown on the line
    }

    private sealed class CharElement : Element
    {
        private readonly char _c;
        public CharElement(char c) => _c = c;
        public override string Value => _c.ToString();
        // A literal newline inside a short inline paste is shown as a marker so the single-line
        // display never actually wraps to a new row.
        public override string Display => _c == '\n' ? "⏎" : _c.ToString();
    }

    private sealed class PasteElement : Element
    {
        private readonly string _text;
        private readonly int _lines;
        public PasteElement(string text)
        {
            _text = text;
            _lines = text.Count(c => c == '\n') + 1;
        }
        public override string Value => _text;
        public override string Display => $"[pasted {_lines} line{(_lines == 1 ? "" : "s")}]";
    }

    private readonly List<Element> _elements = new();
    private int _cursor; // 0 .. _elements.Count

    public bool IsEmpty => _elements.Count == 0;
    public int Cursor => _cursor;
    public int Count => _elements.Count;

    public void InsertChar(char c)
    {
        _elements.Insert(_cursor, new CharElement(c));
        _cursor++;
    }

    public void InsertText(string s)
    {
        foreach (var c in s)
        {
            _elements.Insert(_cursor, new CharElement(c));
            _cursor++;
        }
    }

    /// <summary>Insert a collapsed paste block (rendered as a single chip) at the cursor.</summary>
    public void InsertPaste(string text)
    {
        _elements.Insert(_cursor, new PasteElement(text));
        _cursor++;
    }

    public void Backspace()
    {
        if (_cursor > 0)
        {
            _cursor--;
            _elements.RemoveAt(_cursor);
        }
    }

    public void Delete()
    {
        if (_cursor < _elements.Count)
            _elements.RemoveAt(_cursor);
    }

    public void MoveLeft() { if (_cursor > 0) _cursor--; }
    public void MoveRight() { if (_cursor < _elements.Count) _cursor++; }
    public void Home() => _cursor = 0;
    public void End() => _cursor = _elements.Count;

    public void Clear()
    {
        _elements.Clear();
        _cursor = 0;
    }

    /// <summary>Delete from the cursor to the end of the line (Ctrl+K).</summary>
    public void KillToEnd()
    {
        while (_elements.Count > _cursor)
            _elements.RemoveAt(_elements.Count - 1);
    }

    /// <summary>Delete from the start of the line to the cursor (Ctrl+U).</summary>
    public void KillToStart()
    {
        for (var i = 0; i < _cursor; i++) _elements.RemoveAt(0);
        _cursor = 0;
    }

    /// <summary>The single-line rendering (paste chips + ⏎ markers for inline newlines).</summary>
    public string Display()
    {
        var sb = new StringBuilder();
        foreach (var e in _elements) sb.Append(e.Display);
        return sb.ToString();
    }

    /// <summary>Column the cursor sits at within <see cref="Display"/> (in display characters).</summary>
    public int CursorDisplayColumn
    {
        get
        {
            var col = 0;
            for (var i = 0; i < _cursor; i++) col += _elements[i].Display.Length;
            return col;
        }
    }

    /// <summary>The real text to submit — paste blocks expanded, ⏎ back to real newlines.</summary>
    public string Resolve()
    {
        var sb = new StringBuilder();
        foreach (var e in _elements) sb.Append(e.Value);
        return sb.ToString();
    }
}
