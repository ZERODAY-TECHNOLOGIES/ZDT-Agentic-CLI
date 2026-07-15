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
