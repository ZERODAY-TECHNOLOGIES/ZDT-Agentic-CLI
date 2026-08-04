using System.Text;
using Spectre.Console;
using Zdtllm.Core;
using Zdtllm.Core.Repl;
using Zdtllm.Core.Tui;
using Zdtllm.Tools;

namespace Zdtllm.Cli.Tui;

/// <summary>
/// A claude-code-style interactive layout: model/tool output scrolls in the upper part of the
/// terminal while a persistent, always-writable multi-line input box stays pinned at the bottom —
/// you can type (and navigate with the arrow keys) whether the model is thinking or idle. Submitting
/// while a turn runs queues the message (folded into the run); submitting when idle starts a turn.
///
/// <para>
/// Mechanism: a DECSTBM scroll region reserves the top rows for output; the bottom box (a status
/// line, the input lines, a footer with the mode / bypass indicator) is redrawn manually under a
/// render lock. A single background reader owns the keyboard. All console access is guarded and the
/// scroll region is reset on dispose; the whole thing is opt-in (interactive ANSI TTY, ZDT_NO_TUI to
/// disable) with a plain-REPL fallback.
/// </para>
///
/// <para>
/// Rendering discipline (the anti-flicker / anti-ghosting rules):
/// <list type="bullet">
/// <item>Every paint is composed into ONE string and emitted with a single <c>Console.Write</c>,
/// wrapped in DEC 2026 synchronized-output so the terminal presents it atomically.</item>
/// <item>While an exclusive section owns the screen (slash picker, AskUserQuestion, the fleet
/// view's alternate screen) output lines are DEFERRED and box redraws suppressed; everything is
/// flushed when the screen comes back. Nothing may draw over an exclusive renderer.</item>
/// <item>Before handing the screen to an exclusive prompt the box is ERASED — if the prompt
/// scrolls the screen, no stale copy of the box can be dragged into the transcript.</item>
/// <item>The scroll region is re-asserted whenever its computed bottom row changes (box height OR
/// terminal height), never left stale after a resize.</item>
/// <item>When the box shrinks, the vacated rows are cleared so old status/footer rows can't leak
/// into the scrollback as ghost lines.</item>
/// </list>
/// </para>
///
/// It plugs into the existing REPL as its input source (<see cref="IReplInputSource"/>), turn-capture
/// hook (<see cref="ITurnInputCapture"/>) and output writer, so the REPL's slash-commands / turn /
/// farewell logic is reused unchanged.
/// </summary>
public sealed class BottomInputTui : IReplInputSource, ITurnInputCapture, IInteractivePrompter, Zdtllm.Core.AgentFleet.IConsoleExclusive, IDisposable
{
    private const string Reset = "\x1b[0m";
    private const string Cyan = "\x1b[38;2;27;234;205m";
    private const string Gold = "\x1b[38;2;229;217;54m";
    private const string Red = "\x1b[38;2;239;68;68m";
    private const string Mute = "\x1b[38;2;104;123;137m";
    // DEC 2026 synchronized output: the terminal buffers everything between begin/end and presents
    // it as one atomic frame — no half-painted rows, no cursor-jump flicker. Terminals that don't
    // support it (older conhost) simply ignore the sequences.
    private const string SyncBegin = "\x1b[?2026h";
    private const string SyncEnd = "\x1b[?2026l";
    private const int MaxInputRows = 8;

    private readonly IUserInputQueue _queue;
    private readonly IAnsiConsole _spectre;
    private readonly bool _bypassPermissions;
    private readonly IReadOnlyList<SlashCommandInfo> _slashCommands;
    private readonly MultiLineEditor _editor = new();
    private readonly object _render = new();
    private readonly SemaphoreSlim _readerGate = new(1, 1); // held by the key reader; a prompt takes it to pause reading

    private volatile bool _openSlashPicker; // set by HandleKey ("/"), serviced by the reader loop
    // Submitted-message history for shell-style ↑/↓ recall (all mutated under _render, like _editor).
    // _historyIndex: -1 = editing a fresh draft; otherwise an index into _history. _historyDraft holds
    // the in-progress text saved the moment recall began, so ↓ past the newest entry restores it.
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _historyDraft = "";
    private TaskCompletionSource<string?>? _pendingRead;
    private long _thinkingStartTicks; // 0 = idle; otherwise Stopwatch ticks at turn start
    private long _compactingStartTicks; // 0 = not compacting; otherwise Stopwatch ticks at compaction start
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private CancellationTokenSource? _cts;
    private Task? _reader;
    private volatile bool _started;
    private int _disposed;
    private bool _exclusive;                       // guarded by _render: an exclusive renderer owns the screen
    private readonly List<string> _deferred = new(); // guarded by _render: output held back while exclusive
    private int _regionBottom = -1;                // last DECSTBM bottom row actually emitted (-1 = none)
    private int _paintedBoxTop = -1;               // first row of the last painted box (-1 = not painted)
    private int _paintedBoxHeight = -1;            // height of the last painted box
    private int _rows = 24, _cols = 80;

    /// <summary>Write REPL / model output here — it's line-buffered into the scrolling region.</summary>
    public TextWriter Output { get; }

    public BottomInputTui(IUserInputQueue queue, IAnsiConsole spectre, bool bypassPermissions,
        IReadOnlyList<SlashCommandInfo>? slashCommands = null,
        Zdtllm.Tools.IPermissionModeSwitch? permissionMode = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _spectre = spectre ?? throw new ArgumentNullException(nameof(spectre));
        _bypassPermissions = bypassPermissions;
        _slashCommands = slashCommands ?? Array.Empty<SlashCommandInfo>();
        _permMode = permissionMode;
        Output = new LineBufferedWriter(EmitOutputLine);
    }

    /// <summary>Shared permission mode; Shift+Tab cycles it and the footer reflects it. Null → the
    /// static bypass/ask footer (non-interactive or no mode wired).</summary>
    private readonly Zdtllm.Tools.IPermissionModeSwitch? _permMode;

    public bool IsAvailable => true;

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshDims();
        lock (_render)
        {
            var sb = BeginFrame();
            var boxH = BoxHeight();
            // Reserve room below whatever is already on screen (the banner): if the cursor sits
            // near the bottom these LFs scroll just enough that the box won't overwrite the last
            // lines already printed; mid-screen they only move the cursor down.
            sb.Append(Reset).Append('\n', boxH);
            sb.Append("\x1b[?7l");              // disable autowrap so clipped lines never wrap-scroll
            AppendScrollRegionLocked(sb, force: true);
            // Park the output cursor on the last scrollable row.
            sb.Append($"\x1b[{_rows - boxH};1H");
            AppendBoxLocked(sb);
            EndFrame(sb);
        }
        _cts = new CancellationTokenSource();
        _reader = Task.Run(() => ReaderLoop(_cts.Token));
    }

    // ---- IReplInputSource ----

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (!_started) Start();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRead = tcs;
        RedrawBox(); // reflect idle prompt
        await using (ct.Register(() => tcs.TrySetResult(null)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    // ---- ITurnInputCapture (thinking indicator; the box is always capturing) ----

    public void BeginCapture()
    {
        Interlocked.Exchange(ref _thinkingStartTicks, _clock.ElapsedTicks == 0 ? 1 : _clock.ElapsedTicks);
        TerminalStatus.Working();   // taskbar/tab: "working"
        RedrawBox();
    }

    public Task EndCaptureAsync()
    {
        Interlocked.Exchange(ref _thinkingStartTicks, 0);
        TerminalStatus.Idle();      // taskbar/tab: "ready" + flash
        RedrawBox();
        return Task.CompletedTask;
    }

    // Animated "compacting…" indicator for the status row, for the lifetime of the returned handle.
    // Works whether or not a turn is active: the reader loop already redraws the status row on a
    // timer while thinking OR compacting, and StatusText shows compacting with priority. During
    // manual /compact the box is idle (no thinking), so this is the only thing animating; during
    // mid-turn auto-compact it briefly supersedes the "thinking" label, which resumes on dispose.
    public IDisposable BeginCompacting()
    {
        Interlocked.Exchange(ref _compactingStartTicks, _clock.ElapsedTicks == 0 ? 1 : _clock.ElapsedTicks);
        TerminalStatus.Working();
        RedrawStatusRow();
        return new CompactingScope(this);
    }

    private void EndCompacting()
    {
        Interlocked.Exchange(ref _compactingStartTicks, 0);
        RedrawStatusRow();
    }

    private sealed class CompactingScope : IDisposable
    {
        private BottomInputTui? _tui;
        public CompactingScope(BottomInputTui tui) => _tui = tui;
        public void Dispose() { var t = _tui; _tui = null; t?.EndCompacting(); }
    }

    // ---- reader loop ----

    private void ReaderLoop(CancellationToken ct)
    {
        var lastTick = _clock.ElapsedMilliseconds;
        while (!ct.IsCancellationRequested)
        {
            // Yield the keyboard while an exclusive prompt (AskUserQuestion / ExitPlanMode) runs.
            if (!_readerGate.Wait(0)) { Thread.Sleep(8); continue; }
            try
            {
                var handled = false;
                var batch = new List<ConsoleKeyInfo>();
                while (SafeKeyAvailable() && batch.Count < 8192)
                    batch.Add(Console.ReadKey(intercept: true));

                // Editor mutations happen under the render lock: other threads snapshot
                // _editor.Lines while painting, and List<T> must never be enumerated mid-insert.
                if (batch.Count == 1) { lock (_render) HandleKey(batch[0]); handled = true; }
                else if (batch.Count > 1)
                {
                    // A burst = paste: insert literally (newlines kept), don't submit.
                    var sb = new StringBuilder();
                    foreach (var k in batch)
                        if (k.Key == ConsoleKey.Enter) sb.Append('\n');
                        else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
                    lock (_render) { _editor.InsertText(sb.ToString()); _historyIndex = -1; }
                    handled = true;
                }

                var now = _clock.ElapsedMilliseconds;
                if (handled)
                {
                    RedrawBox();
                    lastTick = now;
                }
                else if ((Volatile.Read(ref _thinkingStartTicks) != 0
                          || Volatile.Read(ref _compactingStartTicks) != 0) && now - lastTick > 180)
                {
                    // ~5x/sec while thinking or compacting: only the status row changes — repaint just it.
                    RedrawStatusRow();
                    lastTick = now;
                }
            }
            catch { /* never let the reader die */ }
            finally { _readerGate.Release(); }

            // Service a pending "/" picker request now that the gate is released (RunExclusiveAsync
            // re-takes it). Runs on this reader thread; blocking here is fine — the whole point is to
            // pause reading while the picker owns the keyboard.
            if (_openSlashPicker)
            {
                _openSlashPicker = false;
                RunSlashPicker();
                lastTick = _clock.ElapsedMilliseconds;
            }

            if (!SafeKeyAvailable()) Thread.Sleep(12);
        }
    }

    private void RunSlashPicker()
    {
        try
        {
            var chosen = RunExclusiveAsync(() =>
                Input.SpectreChoice.SelectSlashCommandAsync(_spectre, _slashCommands, CancellationToken.None))
                .GetAwaiter().GetResult();
            lock (_render)
            {
                if (chosen is not null) _editor.InsertText(chosen + " ");
                else _editor.InsertChar('/');   // cancelled → keep the slash for manual typing
            }
        }
        catch { /* picker failed — leave the box as-is */ }
        RedrawBox();
    }

    // ---- IInteractivePrompter (AskUserQuestion / ExitPlanMode) ----

    public async Task<IReadOnlyList<string>> SelectAsync(
        string question, string? header, IReadOnlyList<PromptChoice> options,
        bool multiSelect, bool allowFreeText, CancellationToken ct)
    {
        // Reuse ConsoleInput's Spectre-backed prompter, but under this TUI's exclusive section so the
        // reader thread + scroll region don't fight the prompt. We build a throwaway prompter view.
        return await RunExclusiveAsync(() =>
            Input.SpectreChoice.SelectAsync(_spectre, question, header, options, multiSelect, allowFreeText, ct))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Run <paramref name="action"/> with sole ownership of the console: pause the key reader,
    /// erase the box and lift the scroll region so Spectre can render + read keys normally, then
    /// restore the region, flush deferred output, and repaint the box afterwards.
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _readerGate.WaitAsync().ConfigureAwait(false);
        LiftForExclusive();
        try { return await action().ConfigureAwait(false); }
        finally { RestoreFromExclusive(); _readerGate.Release(); }
    }

    // ---- IConsoleExclusive (hand the screen to the agent fleet view) ----

    /// <summary>
    /// Pause the reader and hand the whole terminal to a full-screen renderer (the fleet view) on the
    /// <b>alternate screen buffer</b> — like vim/less: the view gets a pristine screen, and disposing
    /// the handle switches back to the main buffer, restoring the conversation + input box exactly as
    /// they were, then re-asserts the scroll region and resumes the reader. While the view owns the
    /// screen, all TUI output is deferred (flushed on return) so nothing can paint over it.
    /// Synchronous counterpart of <see cref="RunExclusiveAsync{T}"/> (the fleet view's blocking
    /// render loop runs on its own thread, so the blocking wait is fine).
    /// </summary>
    public IDisposable EnterExclusive()
    {
        _readerGate.Wait();
        lock (_render)
        {
            _exclusive = true;                // from here on, output defers and box redraws no-op
            Console.Write(
                "\x1b[?1049h" +               // enter alternate screen buffer (fresh, isolated)
                "\x1b[r" +                    // no scroll region on the alt screen
                "\x1b[?7h" +                  // autowrap on — Spectre expects it
                "\x1b[H\x1b[2J");             // home + clear the alt screen
            _regionBottom = -1;               // force region re-apply on restore
            _paintedBoxTop = -1;
        }
        return new ExclusiveScope(this);
    }

    // Leave the alternate screen: the main buffer (conversation + box) comes back verbatim; still
    // re-assert the region + repaint so output resumes correctly regardless of whether the terminal
    // preserved our DECSTBM margins across the buffer switch. Deferred output is flushed here.
    private void RestoreFromAltScreen()
    {
        lock (_render)
        {
            _exclusive = false;
            var sb = BeginFrame();
            sb.Append("\x1b[?7l");            // autowrap off again (our clipping model)
            sb.Append("\x1b[?1049l");         // back to the main screen buffer
            AppendScrollRegionLocked(sb, force: true);
            FlushDeferredLocked(sb);
            AppendBoxLocked(sb);
            EndFrame(sb);
        }
    }

    // Holds nothing on entry; takes _render to emit the lift sequence. The box is ERASED before
    // the exclusive renderer takes over: if its output scrolls the (now region-less) screen, there
    // is no pinned box left to be dragged up into the transcript as a ghost copy.
    private void LiftForExclusive()
    {
        lock (_render)
        {
            var top = _paintedBoxTop > 0 ? _paintedBoxTop : _rows - BoxHeight() + 1;
            var sb = BeginFrame();
            sb.Append("\x1b[r");                          // lift scroll region (homes the cursor)
            sb.Append("\x1b[?7h");                        // restore autowrap (Spectre expects it)
            sb.Append($"\x1b[{top};1H{Reset}\x1b[0J");    // erase the box; prompt renders in its place
            EndFrame(sb);
            _regionBottom = -1;                           // force region re-apply on restore
            _paintedBoxTop = -1;
            _exclusive = true;                            // defer output until restore
        }
    }

    private void RestoreFromExclusive()
    {
        lock (_render)
        {
            _exclusive = false;
            var sb = BeginFrame();
            sb.Append("\x1b[?7l");                        // autowrap off again (our clipping model)
            AppendScrollRegionLocked(sb, force: true);
            FlushDeferredLocked(sb);
            AppendBoxLocked(sb);
            EndFrame(sb);
        }
    }

    private sealed class ExclusiveScope : IDisposable
    {
        private BottomInputTui? _tui;
        public ExclusiveScope(BottomInputTui tui) => _tui = tui;
        public void Dispose()
        {
            var t = _tui; _tui = null;
            if (t is null) return;
            t.RestoreFromAltScreen();
            t._readerGate.Release();
        }
    }

    // Holds _render (called from the reader loop under the lock).
    private void HandleKey(ConsoleKeyInfo k)
    {
        if ((k.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (k.Key)
            {
                case ConsoleKey.D: if (_editor.IsEmpty) SubmitOrExit(null); return;
                case ConsoleKey.A: _editor.Home(); return;
                case ConsoleKey.E: _editor.End(); return;
                case ConsoleKey.U: _editor.KillToStart(); return;
                case ConsoleKey.K: _editor.KillToEnd(); return;
            }
        }

        switch (k.Key)
        {
            case ConsoleKey.Enter:
                // Alt+Enter (or a trailing backslash) inserts a newline; a plain Enter submits.
                if ((k.Modifiers & ConsoleModifiers.Alt) != 0) { _editor.Newline(); _historyIndex = -1; return; }
                var text = _editor.Text;
                if (text.EndsWith("\\", StringComparison.Ordinal))
                {
                    _editor.Backspace();     // drop the trailing backslash
                    _editor.Newline();
                    _historyIndex = -1;
                    return;
                }
                SubmitOrExit(text);
                return;
            case ConsoleKey.Backspace: _editor.Backspace(); _historyIndex = -1; return;
            case ConsoleKey.Delete: _editor.Delete(); _historyIndex = -1; return;
            case ConsoleKey.LeftArrow: _editor.Left(); return;
            case ConsoleKey.RightArrow: _editor.Right(); return;
            case ConsoleKey.UpArrow: HistoryUp(); return;
            case ConsoleKey.DownArrow: HistoryDown(); return;
            case ConsoleKey.Home: _editor.Home(); return;
            case ConsoleKey.End: _editor.End(); return;
            case ConsoleKey.Escape: return;
            case ConsoleKey.Tab when (k.Modifiers & ConsoleModifiers.Shift) != 0:
                // Shift+Tab cycles the permission mode (Default → AcceptEdits → Plan). The footer
                // repaints on the next frame; the shared switch is read live by the AgentLoop.
                _permMode?.Cycle();
                return;
        }

        // Typing "/" on an empty box opens the slash-command autocomplete picker (same list the REPL
        // shows). We can't run it here — this method holds the reader gate — so flag it and let the
        // reader loop service it once the gate is free.
        if (k.KeyChar == '/' && _editor.IsEmpty && _slashCommands.Count > 0)
        {
            _openSlashPicker = true;
            return;
        }

        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
        {
            _editor.InsertChar(k.KeyChar);
            _historyIndex = -1; // typing commits to editing this text, not navigating history
        }
    }

    // ↑: inside a multi-line buffer, move up a line; on the FIRST line, recall the previous submitted
    // message (shell / claude-code style). The current draft is saved the first time you step in.
    // Holds _render (called from HandleKey under the lock).
    private void HistoryUp()
    {
        if (_editor.CursorRow > 0) { _editor.Up(); return; }
        if (_history.Count == 0) return;
        if (_historyIndex == -1) { _historyDraft = _editor.Text; _historyIndex = _history.Count; }
        if (_historyIndex > 0) { _historyIndex--; ReplaceEditorText(_history[_historyIndex]); }
    }

    // ↓: inside a multi-line buffer, move down a line; on the LAST line, walk forward through history
    // and finally back to the draft you were composing. Holds _render.
    private void HistoryDown()
    {
        if (_editor.CursorRow < _editor.LineCount - 1) { _editor.Down(); return; }
        if (_historyIndex == -1) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count) { _historyIndex = -1; ReplaceEditorText(_historyDraft); }
        else ReplaceEditorText(_history[_historyIndex]);
    }

    // Holds _render.
    private void ReplaceEditorText(string text)
    {
        _editor.Clear();
        _editor.InsertText(text); // cursor lands at the end
    }

    // Holds _render. Record a just-submitted message for ↑/↓ recall (drop consecutive duplicates).
    private void PushHistory(string text)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], text, StringComparison.Ordinal))
            _history.Add(text);
        _historyIndex = -1;
        _historyDraft = "";
    }

    /// <summary>
    /// Pre-load prior submitted messages (oldest→newest, e.g. the user turns from a resumed session)
    /// so ↑/↓ can recall them immediately — before any new submission this run.
    /// </summary>
    public void SeedHistory(IEnumerable<string> pastMessages)
    {
        lock (_render)
        {
            foreach (var m in pastMessages)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                if (_history.Count == 0 || !string.Equals(_history[^1], m, StringComparison.Ordinal))
                    _history.Add(m);
            }
            _historyIndex = -1;
        }
    }

    private void SubmitOrExit(string? text)
    {
        // Ctrl+D on an empty box → EOF (exit). Otherwise submit the trimmed text.
        if (text is null)
        {
            _pendingRead?.TrySetResult(null);
            _pendingRead = null;
            return;
        }

        var trimmed = text.Trim();
        _editor.Clear();
        if (trimmed.Length == 0) { RedrawBox(); return; }

        PushHistory(trimmed); // record for ↑/↓ recall (both idle-start and queued submissions)

        var pending = _pendingRead;
        if (pending is not null && pending.TrySetResult(trimmed))
        {
            _pendingRead = null; // idle submission → starts a turn
        }
        else
        {
            _queue.Enqueue(trimmed); // a turn is running → queue it
            EchoQueuedLine(trimmed);
        }
        RedrawBox();
    }

    // ---- rendering ----

    // Every paint path composes ONE frame into a StringBuilder and writes it in a single
    // Console.Write, wrapped in DEC 2026 synchronized output. One write = no interleaving with
    // other writers mid-frame; sync markers = the terminal presents it atomically (no flicker).
    private static StringBuilder BeginFrame() => new StringBuilder(256).Append(SyncBegin);

    private static void EndFrame(StringBuilder sb)
    {
        sb.Append(SyncEnd);
        Console.Write(sb.ToString());
    }

    private void EmitOutputLine(string line)
    {
        lock (_render)
        {
            if (!_started) { Console.WriteLine(line); return; }
            if (_exclusive) { _deferred.Add(line); return; } // never paint over an exclusive renderer
            var sb = BeginFrame();
            AppendOutputLineLocked(sb, line);
            // If the box height changed since the last paint (the user grew/shrank the input while
            // output streamed), repaint it so vacated rows are cleared; otherwise just re-park the
            // cursor. The per-line full-box flood is what garbled multi-agent output historically —
            // keep the common path to "append one line + re-park".
            if (BoxHeight() != _paintedBoxHeight) AppendBoxLocked(sb);
            else AppendParkCursorLocked(sb);
            EndFrame(sb);
        }
    }

    // Holds _render. Append one line at the bottom of the scroll region: write it on the last
    // scrollable row, clear any leftovers, then LF. LF at the bottom of a DECSTBM region scrolls
    // only that region up by one — the pinned box below is untouched. Autowrap is off so a
    // full-width line can't wrap-scroll.
    private void AppendOutputLineLocked(StringBuilder sb, string line)
    {
        RefreshDims();
        AppendScrollRegionLocked(sb, force: false); // keep the region in sync if rows/box changed
        var outRow = _rows - BoxHeight();
        sb.Append($"\x1b[{outRow};1H");
        sb.Append(Clip(line, _cols - 1));
        sb.Append(Reset).Append("\x1b[K\n");
    }

    // Holds _render. Flush output that arrived while an exclusive section owned the screen.
    private void FlushDeferredLocked(StringBuilder sb)
    {
        if (_deferred.Count == 0) return;
        foreach (var line in _deferred) AppendOutputLineLocked(sb, line);
        _deferred.Clear();
    }

    // Holds _render. Move the visible cursor back into the input box without repainting the box.
    private void AppendParkCursorLocked(StringBuilder sb)
    {
        var boxH = BoxHeight();
        var top = _rows - boxH + 1;
        var inputRows = Math.Clamp(_editor.LineCount, 1, MaxInputRows);
        var firstLine = Math.Max(0, Math.Min(_editor.CursorRow - (inputRows - 1), _editor.LineCount - inputRows));
        var curScreenRow = top + 1 + (_editor.CursorRow - firstLine);
        var curCol = 2 + Math.Min(_editor.CursorCol, _cols - 4) + 1;
        sb.Append($"\x1b[{curScreenRow};{curCol}H");
    }

    private void EchoQueuedLine(string text) =>
        EmitOutputLine($"{Cyan}⏳ queued:{Reset} {Mute}{Clip(text.Replace('\n', ' '), 72)}{Reset}");

    private void RedrawBox()
    {
        lock (_render)
        {
            if (!_started || _exclusive) return;
            var sb = BeginFrame();
            AppendBoxLocked(sb);
            EndFrame(sb);
        }
    }

    /// <summary>Fast path for the thinking-timer tick: only the status row changes — rewrite just
    /// that row and re-park the cursor, instead of flooding the whole box 5x/sec.</summary>
    private void RedrawStatusRow()
    {
        lock (_render)
        {
            if (!_started || _exclusive || _paintedBoxTop < 1) return;
            var sb = BeginFrame();
            AppendRowLocked(sb, _paintedBoxTop, StatusText());
            AppendParkCursorLocked(sb);
            EndFrame(sb);
        }
    }

    // Holds _render.
    private void AppendBoxLocked(StringBuilder sb)
    {
        RefreshDims();
        AppendScrollRegionLocked(sb, force: false);

        var boxH = BoxHeight();
        var top = _rows - boxH + 1;           // first box row (1-based)
        var lines = _editor.Lines;
        var inputRows = Math.Clamp(lines.Count, 1, MaxInputRows);

        // If the box shrank (its top moved DOWN), the vacated rows above the new top are now part
        // of the scroll region and still hold old status/input content — clear them, or they scroll
        // into the transcript as ghost box fragments.
        if (_paintedBoxTop > 0 && _paintedBoxTop < top)
            for (var r = _paintedBoxTop; r < top; r++)
                sb.Append($"\x1b[{r};1H\x1b[2K");
        _paintedBoxTop = top;
        _paintedBoxHeight = boxH;

        // Status line.
        AppendRowLocked(sb, top, StatusText());

        // Input lines (first prefixed "> ", continuations "  "). Show a window if there are more
        // lines than fit, keeping the cursor row visible.
        var firstLine = Math.Max(0, Math.Min(_editor.CursorRow - (inputRows - 1), lines.Count - inputRows));
        for (var i = 0; i < inputRows; i++)
        {
            var li = firstLine + i;
            var prefix = li == 0 ? $"{Cyan}>{Reset} " : "  ";
            var content = li < lines.Count ? lines[li] : "";
            AppendRowLocked(sb, top + 1 + i, prefix + Clip(content, _cols - 3));
        }

        // Footer.
        AppendRowLocked(sb, top + 1 + inputRows, FooterText());

        // Park the visible cursor at the editor position inside the input area.
        var curScreenRow = top + 1 + (_editor.CursorRow - firstLine);
        var curCol = 2 /* "> " */ + Math.Min(_editor.CursorCol, _cols - 4) + 1; // 1-based
        sb.Append($"\x1b[{curScreenRow};{curCol}H");
    }

    private string StatusText()
    {
        // Compacting takes priority: it can run mid-turn (thinking also set) and is the more
        // specific thing happening. Same animated-dots + elapsed style as the thinking indicator.
        var compacting = Volatile.Read(ref _compactingStartTicks);
        if (compacting != 0)
        {
            var secs = (int)TimeSpan.FromTicks(_clock.ElapsedTicks - compacting + 1).TotalSeconds;
            var dots = new string('.', 1 + (int)((_clock.ElapsedMilliseconds / 400) % 3));
            return $"{Cyan}⏺ compacting conversation{dots}{Reset} {Mute}({secs}s · summarising older turns){Reset}";
        }
        var thinking = Volatile.Read(ref _thinkingStartTicks);
        if (thinking != 0)
        {
            var secs = (int)TimeSpan.FromTicks(_clock.ElapsedTicks - thinking + 1).TotalSeconds;
            var dots = new string('.', 1 + (int)((_clock.ElapsedMilliseconds / 400) % 3));
            return $"{Cyan}⏺ thinking{dots}{Reset} {Mute}({secs}s · type to queue a message){Reset}";
        }
        return $"{Mute}─── ready ─ Enter to send · \\+Enter or Alt+Enter for newline{Reset}";
    }

    private string FooterText()
    {
        string mode;
        if (_permMode is not null)
        {
            mode = _permMode.Mode switch
            {
                Zdtllm.Tools.PermissionMode.Bypass => $"{Red}⚠ bypass{Reset}",
                Zdtllm.Tools.PermissionMode.Plan => $"{Cyan}⏸ plan{Reset}",
                Zdtllm.Tools.PermissionMode.AcceptEdits => $"{Cyan}✎ accept-edits{Reset}",
                _ => $"{Mute}permissions: ask{Reset}",
            };
            mode += $"  {Mute}(⇧⇥ mode){Reset}";
        }
        else
        {
            mode = _bypassPermissions ? $"{Red}⚠ bypass permissions ON{Reset}" : $"{Mute}permissions: ask{Reset}";
        }
        return $"{mode}  {Mute}·  / commands · Ctrl+C interrupt/exit{Reset}";
    }

    // Holds _render. Write a full box row: position, content, then reset + clear-to-EOL. Writing
    // content first and clearing the tail (instead of 2K-then-write) avoids the blank-row flash on
    // terminals without synchronized-output support.
    private void AppendRowLocked(StringBuilder sb, int row, string content)
    {
        if (row < 1 || row > _rows) return;
        sb.Append($"\x1b[{row};1H");
        sb.Append(content);
        sb.Append(Reset).Append("\x1b[K");
    }

    private int BoxHeight()
    {
        var inputRows = Math.Clamp(_editor.LineCount, 1, MaxInputRows);
        return 1 /*status*/ + inputRows + 1 /*footer*/;
    }

    // Holds _render. Re-asserts the DECSTBM region whenever its computed bottom row changed —
    // whether from a box-height change OR a terminal resize. A stale region is what turns
    // region-bottom LFs into whole-screen scrolls (ghost boxes everywhere). Note DECSTBM homes the
    // cursor, so every caller must position the cursor afterwards.
    private void AppendScrollRegionLocked(StringBuilder sb, bool force)
    {
        var bottom = Math.Max(1, _rows - BoxHeight());
        if (!force && bottom == _regionBottom) return;
        _regionBottom = bottom;
        sb.Append($"\x1b[1;{bottom}r");
    }

    private void RefreshDims()
    {
        try
        {
            var h = Console.WindowHeight;
            var w = Console.WindowWidth;
            if (h > 4) _rows = h;
            if (w > 10) _cols = w;
        }
        catch { /* keep last known */ }
    }

    private static string Clip(string s, int max)
    {
        if (max <= 0) return "";
        // Strip embedded newlines for single-row rendering; truncate to VISIBLE width. The
        // truncation must be ANSI-aware: counting raw chars would cut colored lines way short
        // and could slice through an escape sequence, spraying "[0m" fragments into the
        // transcript. Escape sequences are copied wholesale and never counted.
        s = s.Replace("\r", "").Replace("\n", "⏎");
        if (s.IndexOf('\x1b') < 0)
            return s.Length <= max ? s : s[..max];

        var sb = new StringBuilder(s.Length);
        var visible = 0;
        var truncated = false;
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '\x1b')
            {
                var start = i;
                i++; // consume ESC
                if (i < s.Length && s[i] == '[')
                {
                    i++; // CSI: parameters/intermediates then one final byte in @-~
                    while (i < s.Length && (s[i] < '@' || s[i] > '~')) i++;
                    if (i < s.Length) i++;
                }
                else if (i < s.Length && s[i] == ']')
                {
                    i++; // OSC: terminated by BEL or ST (ESC \)
                    while (i < s.Length && s[i] != '\x07' && s[i] != '\x1b') i++;
                    if (i < s.Length) i += s[i] == '\x07' ? 1 : Math.Min(2, s.Length - i);
                }
                else if (i < s.Length) i++; // two-char escape
                sb.Append(s, start, i - start);
                continue;
            }
            if (visible >= max) { truncated = true; break; }
            sb.Append(s[i]);
            visible++;
            i++;
        }
        if (truncated) sb.Append(Reset); // never leave a color running past the cut
        return sb.ToString();
    }

    private static bool SafeKeyAvailable()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        // Idempotent: the normal finally AND the ProcessExit hook (hard Ctrl+C exits) both call
        // this; only the first reset sequence should be written.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts?.Cancel(); } catch { }
        try { _reader?.Wait(TimeSpan.FromMilliseconds(400)); } catch { }
        _cts?.Dispose();
        if (_started)
        {
            try
            {
                lock (_render)
                {
                    Console.Write(
                        SyncEnd +                     // never leave a sync frame open
                        "\x1b[?1049l" +               // leave the alt screen if a fleet view raced teardown
                        "\x1b[r" +                    // reset scroll region
                        "\x1b[?7h" +                  // restore autowrap
                        $"\x1b[{_rows};1H\x1b[0m\n"); // cursor to bottom
                    // Don't swallow output that was deferred behind an exclusive section.
                    foreach (var line in _deferred) Console.WriteLine(line);
                    _deferred.Clear();
                }
            }
            catch { }
        }
    }
}
