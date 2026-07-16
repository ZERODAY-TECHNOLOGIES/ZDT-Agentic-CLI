using System.Diagnostics;

namespace Zdtllm.Core.AgentFleet;

public enum AgentRunStatus { Running, Done, Failed }

/// <summary>An immutable view of one agent for rendering.</summary>
public sealed record AgentSnapshot(
    int Id,
    string Label,
    AgentRunStatus Status,
    TimeSpan Elapsed,
    IReadOnlyList<string> RecentLines,
    bool Focused);

/// <summary>
/// The thread-safe state behind the interactive "fleet view" — the list of subagents currently (or
/// recently) running, each with a rolling buffer of its output lines, plus which one has focus.
/// Pure logic (no console): agents register/append/complete from their own threads, the UI reads
/// snapshots and moves focus. This is the unit-testable heart of navigating between agents; the
/// Spectre rendering + key handling is a thin shell on top.
/// </summary>
public sealed class AgentFleetModel
{
    private sealed class Agent
    {
        public int Id;
        public string Label = "";
        public AgentRunStatus Status = AgentRunStatus.Running;
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public readonly List<string> Lines = new();
    }

    private readonly object _lock = new();
    private readonly List<Agent> _agents = new();
    private int _nextId = 1;
    private int _focus;

    /// <summary>Max output lines kept per agent (older lines are dropped).</summary>
    public int MaxLinesPerAgent { get; init; } = 2000;

    public int Register(string label)
    {
        lock (_lock)
        {
            var a = new Agent { Id = _nextId++, Label = label ?? "agent" };
            _agents.Add(a);
            return a.Id;
        }
    }

    public void Append(int id, string line)
    {
        if (line is null) return;
        lock (_lock)
        {
            var a = Find(id);
            if (a is null) return;
            a.Lines.Add(line);
            if (a.Lines.Count > MaxLinesPerAgent) a.Lines.RemoveRange(0, a.Lines.Count - MaxLinesPerAgent);
        }
    }

    public void Complete(int id, bool failed)
    {
        lock (_lock)
        {
            var a = Find(id);
            if (a is null) return;
            a.Status = failed ? AgentRunStatus.Failed : AgentRunStatus.Done;
            a.Watch.Stop();
        }
    }

    public int Count { get { lock (_lock) return _agents.Count; } }

    public int ActiveCount { get { lock (_lock) return _agents.Count(a => a.Status == AgentRunStatus.Running); } }

    public int FocusIndex { get { lock (_lock) return _focus; } }

    public void FocusNext() { lock (_lock) { if (_agents.Count > 0) _focus = (_focus + 1) % _agents.Count; } }

    public void FocusPrev() { lock (_lock) { if (_agents.Count > 0) _focus = (_focus - 1 + _agents.Count) % _agents.Count; } }

    /// <summary>Focus a specific zero-based index (e.g. number-key jump). Out-of-range is ignored.</summary>
    public void Focus(int index) { lock (_lock) { if (index >= 0 && index < _agents.Count) _focus = index; } }

    /// <summary>Snapshot of every agent (for the sidebar), focus flag set on the focused one.</summary>
    public IReadOnlyList<AgentSnapshot> Snapshot(int recentLines = 0)
    {
        lock (_lock)
        {
            var result = new List<AgentSnapshot>(_agents.Count);
            for (var i = 0; i < _agents.Count; i++)
                result.Add(ToSnapshot(_agents[i], i == _focus, recentLines));
            return result;
        }
    }

    /// <summary>Snapshot of just the focused agent, with its last <paramref name="recentLines"/> lines.</summary>
    public AgentSnapshot? Focused(int recentLines)
    {
        lock (_lock)
        {
            if (_agents.Count == 0) return null;
            var i = Math.Clamp(_focus, 0, _agents.Count - 1);
            return ToSnapshot(_agents[i], true, recentLines);
        }
    }

    private static AgentSnapshot ToSnapshot(Agent a, bool focused, int recentLines)
    {
        IReadOnlyList<string> lines = recentLines <= 0 || a.Lines.Count <= recentLines
            ? a.Lines.ToArray()
            : a.Lines.GetRange(a.Lines.Count - recentLines, recentLines).ToArray();
        return new AgentSnapshot(a.Id, a.Label, a.Status, a.Watch.Elapsed, lines, focused);
    }

    private Agent? Find(int id) => _agents.FirstOrDefault(a => a.Id == id);
}
