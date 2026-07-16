using System.Collections.Concurrent;

namespace Zdtllm.Core;

/// <summary>
/// Thread-safe FIFO of messages the user typed WHILE a turn was already running. In
/// claude-cli you can keep typing while the model works and your messages queue up; this is
/// the shared conduit that makes the same possible here.
///
/// <para>
/// Two consumers pull from the same queue, never simultaneously:
/// <list type="bullet">
/// <item><b>AgentLoop</b>, between tool rounds of the in-flight turn, so a queued message is
/// folded into the ongoing task the moment the model next asks the human's side to speak
/// (right after tool results).</item>
/// <item><b>The REPL</b>, after the turn returns, to drain anything still queued (e.g. the model
/// answered without ever calling a tool) and run each as a follow-up turn.</item>
/// </list>
/// The producer is the REPL's console input capture, which runs on a background reader while a
/// turn is active.
/// </para>
/// </summary>
public interface IUserInputQueue
{
    /// <summary>Enqueue a message typed during a turn. Blank input is ignored.</summary>
    void Enqueue(string message);

    /// <summary>Pull the next queued message, or return false when the queue is empty.</summary>
    bool TryDequeue(out string message);

    /// <summary>True when at least one message is waiting.</summary>
    bool HasPending { get; }

    /// <summary>Number of messages currently waiting.</summary>
    int Count { get; }
}

/// <summary>
/// Read-only view of what the user is typing WHILE a turn runs, so the AgentLoop can surface it
/// in the live "thinking" spinner — otherwise mid-turn typing is invisible and the queue feels
/// broken ("did it take my input?"). Implemented by the interactive console driver; null in tests
/// and print mode.
/// </summary>
public interface ITypeAheadStatus
{
    /// <summary>The line currently being typed but not yet submitted ("" when nothing is typed).</summary>
    string CurrentInput { get; }

    /// <summary>How many complete messages are queued, waiting to be folded into the run.</summary>
    int QueuedCount { get; }
}

/// <summary>
/// The REPL's idle line reader. The plain path wraps a <see cref="System.IO.TextReader"/>
/// (tests, non-TTY); the interactive path is a full line editor that draws its own prompt and
/// supports multi-line paste and drag-and-drop. Returns null on EOF / exit request.
/// </summary>
public interface IReplInputSource
{
    /// <summary>Read one submitted line (pastes resolved to real text). Null = EOF / exit.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
}

/// <summary>
/// Controls the background console reader that captures keystrokes into the
/// <see cref="IUserInputQueue"/> while a turn is running. The REPL flips it on for the duration
/// of each turn and off once the turn (and its queued follow-ups) are done. Kept as a tiny
/// interface so the REPL stays unit-testable without a real console — tests pass null.
/// </summary>
public interface ITurnInputCapture
{
    /// <summary>Start capturing typed lines into the queue. Safe to call when already capturing.</summary>
    void BeginCapture();

    /// <summary>Stop capturing and wait for the reader to fully quiesce.</summary>
    Task EndCaptureAsync();
}

/// <inheritdoc cref="IUserInputQueue"/>
public sealed class UserInputQueue : IUserInputQueue
{
    private readonly ConcurrentQueue<string> _queue = new();

    public void Enqueue(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _queue.Enqueue(message.Trim());
    }

    public bool TryDequeue(out string message)
    {
        if (_queue.TryDequeue(out var m))
        {
            message = m;
            return true;
        }
        message = string.Empty;
        return false;
    }

    public bool HasPending => !_queue.IsEmpty;

    public int Count => _queue.Count;
}
