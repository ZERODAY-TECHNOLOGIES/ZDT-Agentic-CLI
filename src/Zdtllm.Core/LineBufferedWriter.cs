using System.Text;

namespace Zdtllm.Core;

/// <summary>
/// A <see cref="TextWriter"/> that accumulates writes and invokes a callback once per completed
/// line (newline-terminated), dropping blank lines and stripping <c>\r</c>. Used to turn a
/// subagent's streamed output/status into discrete lines for the fleet view's per-agent buffer.
/// Thread-safe: a subagent may write from several threads (parallel tool dispatch).
/// </summary>
internal sealed class LineBufferedWriter : TextWriter
{
    private readonly Action<string> _onLine;
    private readonly Encoding _encoding;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    public LineBufferedWriter(Action<string> onLine, Encoding? encoding = null)
    {
        _onLine = onLine ?? throw new ArgumentNullException(nameof(onLine));
        _encoding = encoding ?? Encoding.UTF8;
    }

    public override Encoding Encoding => _encoding;

    public override void Write(char value) { lock (_lock) Append(value); }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_lock) foreach (var c in value) Append(c);
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(value)) foreach (var c in value) Append(c);
            Flush();
        }
    }

    public override void WriteLine() { lock (_lock) Flush(); }

    // Holds _lock.
    private void Append(char c)
    {
        if (c == '\n') { Flush(); return; }
        if (c == '\r') return;
        _buffer.Append(c);
    }

    // Holds _lock. Emits the current line (if non-empty) and clears the buffer.
    private new void Flush()
    {
        if (_buffer.Length == 0) return;
        var line = _buffer.ToString();
        _buffer.Clear();
        _onLine(line);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_lock) Flush();
        base.Dispose(disposing);
    }
}
