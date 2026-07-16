using Spectre.Console;
using Spectre.Console.Rendering;
using Zdtllm.Cli.Input;
using Zdtllm.Core.AgentFleet;

namespace Zdtllm.Cli;

/// <summary>
/// The interactive "fleet view": when two or more subagents run at once, this opens a live Spectre
/// display listing them and shows the focused agent's output, which you navigate between with the
/// arrow keys / number keys (q to detach) — like switching between agents in claude-code.
///
/// <para>
/// It only spins up the live display for ≥2 concurrent agents (a single agent just streams its
/// activity, tagged, to stderr — no display to navigate, and no clash with the parent's spinner).
/// While the display is up it owns the keyboard (pausing the REPL's queue-capture reader via
/// <see cref="ConsoleInput.EnterExclusive"/>). Everything is guarded: if Spectre's live display
/// can't run on this terminal it degrades to the tagged-stderr stream so no activity is ever lost.
/// </para>
/// </summary>
public sealed class AgentFleetView : IAgentFleetMonitor, IDisposable
{
    private const string Cyan = "#1BEACD";
    private const string Gold = "#E5D936";
    private const string Green = "#3FB950";
    private const string Red = "#EF4444";
    private const string Mute = "#687B89";

    private readonly IAnsiConsole _console;
    private readonly ConsoleInput? _consoleOwner;
    private readonly AgentFleetModel _model = new();
    private readonly Dictionary<int, string> _labels = new();
    private readonly object _gate = new();

    private volatile bool _liveActive;   // the Spectre live display is currently rendering
    private volatile bool _detached;     // user pressed q — stop rendering, keep streaming to stderr
    private bool _started;               // the live display was started (guards double-start)
    private CancellationTokenSource? _cts;
    private Task? _task;

    public AgentFleetView(IAnsiConsole console, ConsoleInput? consoleOwner)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _consoleOwner = consoleOwner;
    }

    // ---- IAgentFleetMonitor ----

    public int Register(string label)
    {
        var id = _model.Register(label);
        lock (_gate) _labels[id] = label;
        MaybeStart();
        return id;
    }

    public void Append(int agentId, string line)
    {
        _model.Append(agentId, line);
        // Until (or after) the live display is up, echo tagged to stderr so single-agent and
        // pre-view / post-detach activity stays visible.
        if (!_liveActive)
        {
            string label;
            lock (_gate) label = _labels.TryGetValue(agentId, out var l) ? l : "[agent] ";
            lock (_gate) Console.Error.WriteLine(label + line);
        }
    }

    public void Complete(int agentId, bool failed)
    {
        _model.Complete(agentId, failed);
        MaybeStop();
    }

    // ---- lifecycle ----

    private void MaybeStart()
    {
        if (_started || _detached) return;
        if (_model.ActiveCount < 2) return; // navigation only makes sense with multiple live agents
        lock (_gate)
        {
            if (_started || _detached) return;
            _started = true;
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunLoop(_cts.Token));
        }
    }

    private void MaybeStop()
    {
        if (!_started) return;
        if (_model.ActiveCount > 0) return; // wait until every agent has finished
        _cts?.Cancel();
        // Block the final-completing agent briefly until the display tears down, so the parent's
        // next output doesn't overlap the live region. Best-effort.
        try { _task?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
    }

    private void RunLoop(CancellationToken ct)
    {
        using var _ = _consoleOwner?.EnterExclusive(); // own the keyboard; pause queue capture
        _liveActive = true;
        try
        {
            _console.Live(BuildRenderable())
                .AutoClear(false)
                .Start(ctx =>
                {
                    while (!ct.IsCancellationRequested && !_detached)
                    {
                        HandleKeys();
                        ctx.UpdateTarget(BuildRenderable());
                        ctx.Refresh();
                        Thread.Sleep(80);
                    }
                    ctx.UpdateTarget(BuildRenderable()); // final frame
                    ctx.Refresh();
                });
        }
        catch (Exception ex)
        {
            // Live display isn't usable on this terminal — degrade to the tagged stream so nothing
            // is lost, and don't try again.
            _detached = true;
            DumpBufferedToStderr();
            Console.Error.WriteLine($"(agent view unavailable: {ex.Message} — falling back to stream)");
        }
        finally
        {
            _liveActive = false;
        }
    }

    private void HandleKeys()
    {
        while (SafeKeyAvailable())
        {
            var k = Console.ReadKey(intercept: true);
            switch (k.Key)
            {
                case ConsoleKey.LeftArrow:
                case ConsoleKey.UpArrow:
                    _model.FocusPrev();
                    break;
                case ConsoleKey.RightArrow:
                case ConsoleKey.DownArrow:
                case ConsoleKey.Tab:
                    _model.FocusNext();
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    _detached = true;
                    return;
                default:
                    if (k.KeyChar is >= '1' and <= '9')
                        _model.Focus(k.KeyChar - '1');
                    break;
            }
        }
    }

    private IRenderable BuildRenderable()
    {
        var agents = _model.Snapshot();
        var rows = new List<IRenderable>
        {
            new Markup($"[bold {Cyan}]▹ agents[/]  [{Mute}](←/→ or ↑/↓ to switch · 1-9 jump · q detach)[/]"),
        };

        for (var i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            var (icon, color) = a.Status switch
            {
                AgentRunStatus.Running => ("⟳", Cyan),
                AgentRunStatus.Done => ("✓", Green),
                AgentRunStatus.Failed => ("✗", Red),
                _ => ("•", Mute),
            };
            var marker = a.Focused ? $"[bold {Gold}]▸[/]" : " ";
            var num = i < 9 ? $"{i + 1}." : "  ";
            rows.Add(new Markup(
                $"{marker} [{Mute}]{num}[/] [{color}]{icon}[/] " +
                $"{Markup.Escape(a.Label.Trim())} " +
                $"[{Mute}]({(int)a.Elapsed.TotalSeconds}s · {a.RecentLines.Count} ln)[/]"));
        }

        var focused = _model.Focused(recentLines: 18);
        IRenderable body = focused is null || focused.RecentLines.Count == 0
            ? new Markup($"[{Mute}](no output yet)[/]")
            : new Text(string.Join("\n", focused.RecentLines)); // Text = literal, no markup injection

        var header = focused is null
            ? "output"
            : $"[bold {Cyan}]{Markup.Escape(focused.Label.Trim())}[/]";

        rows.Add(new Panel(body)
        {
            Header = new PanelHeader(header),
            Border = BoxBorder.Rounded,
            Expand = true,
        });

        return new Rows(rows);
    }

    private void DumpBufferedToStderr()
    {
        foreach (var a in _model.Snapshot())
        {
            var label = a.Label.Trim();
            foreach (var line in a.RecentLines)
                Console.Error.WriteLine($"[{label}] {line}");
        }
    }

    private static bool SafeKeyAvailable()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _task?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
