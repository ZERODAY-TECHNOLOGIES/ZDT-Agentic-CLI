using System.Text;

namespace Zdtllm.Core;

/// <summary>
/// A <see cref="TextWriter"/> that tags every line it receives with a fixed prefix and forwards it
/// to a shared sink under a shared lock. Used to interleave several subagents' live activity into
/// one stream — each line labelled with which agent produced it (e.g. <c>[Review: a.cs #2] …</c>) —
/// so you can watch what every agent is doing at once, claude-code style.
///
/// <para>
/// Writes are buffered until a newline, then flushed as one prefixed line; blank lines are dropped
/// to keep the trace readable. Fully thread-safe: many subagents run in parallel and each holds its
/// own writer, but they all serialise on the same lock when emitting so lines never interleave
/// mid-line. <c>\r</c> is stripped so Windows line endings don't double up.
/// </para>
/// </summary>
internal sealed class LivePrefixWriter : TextWriter
{
    private readonly TextWriter _sink;
    private readonly object _lock;
    private readonly string _prefix;
    private readonly StringBuilder _buffer = new();

    public LivePrefixWriter(TextWriter sink, object sharedLock, string prefix)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _lock = sharedLock ?? throw new ArgumentNullException(nameof(sharedLock));
        _prefix = prefix ?? string.Empty;
    }

    public override Encoding Encoding => _sink.Encoding;

    public override void Write(char value)
    {
        lock (_lock) Append(value);
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_lock)
            foreach (var c in value) Append(c);
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(value))
                foreach (var c in value) Append(c);
            EmitLocked();
        }
    }

    public override void WriteLine()
    {
        lock (_lock) EmitLocked();
    }

    // Must hold _lock.
    private void Append(char c)
    {
        if (c == '\n') { EmitLocked(); return; }
        if (c == '\r') return;
        _buffer.Append(c);
    }

    // Must hold _lock.
    private void EmitLocked()
    {
        if (_buffer.Length == 0) return; // drop blank lines
        _sink.Write(_prefix);
        _sink.Write(_buffer.ToString());
        _sink.Write(Environment.NewLine);
        _sink.Flush();
        _buffer.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            lock (_lock) EmitLocked(); // flush any trailing partial line
        base.Dispose(disposing);
    }
}
