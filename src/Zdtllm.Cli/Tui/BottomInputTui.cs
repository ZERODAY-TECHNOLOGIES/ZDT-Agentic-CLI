using System.Text;
using Spectre.Console;
using Zdtllm.Core;
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
/// It plugs into the existing REPL as its input source (<see cref="IReplInputSource"/>), turn-capture
/// hook (<see cref="ITurnInputCapture"/>) and output writer, so the REPL's slash-commands / turn /
/// farewell logic is reused unchanged.
/// </summary>
public sealed class BottomInputTui : IReplInputSource, ITurnInputCapture, IInteractivePrompter, IDisposable
{
    private const string Reset = "\x1b[0m";
    private const string Cyan = "\x1b[38;2;27;234;205m";
    private const string Gold = "\x1b[38;2;229;217;54m";
    private const string Red = "\x1b[38;2;239;68;68m";
    private const string Mute = "\x1b[38;2;104;123;137m";
    private const int MaxInputRows = 8;

    private readonly IUserInputQueue _queue;
    private readonly IAnsiConsole _spectre;
    private readonly bool _bypassPermissions;
    private readonly MultiLineEditor _editor = new();
    private readonly object _render = new();
    private readonly SemaphoreSlim _readerGate = new(1, 1); // held by the key reader; a prompt takes it to pause reading

    private TaskCompletionSource<string?>? _pendingRead;
    private long _thinkingStartTicks; // 0 = idle; otherwise Stopwatch ticks at turn start
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private CancellationTokenSource? _cts;
    private Task? _reader;
    private volatile bool _started;
    private int _lastBoxHeight = -1;
    private int _rows = 24, _cols = 80;

    /// <summary>Write REPL / model output here — it's line-buffered into the scrolling region.</summary>
    public TextWriter Output { get; }

    public BottomInputTui(IUserInputQueue queue, IAnsiConsole spectre, bool bypassPermissions)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _spectre = spectre ?? throw new ArgumentNullException(nameof(spectre));
        _bypassPermissions = bypassPermissions;
        Output = new LineBufferedWriter(EmitOutputLine);
    }

    public bool IsAvailable => true;

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshDims();
        lock (_render)
        {
            ApplyScrollRegion(force: true);
            // Park the output cursor on the last scrollable row.
            Console.Write($"\x1b[{_rows - BoxHeight()};1H");
            RedrawBoxLocked();
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
        RedrawBox();
    }

    public Task EndCaptureAsync()
    {
        Interlocked.Exchange(ref _thinkingStartTicks, 0);
        RedrawBox();
        return Task.CompletedTask;
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

                if (batch.Count == 1) { HandleKey(batch[0]); handled = true; }
                else if (batch.Count > 1)
                {
                    // A burst = paste: insert literally (newlines kept), don't submit.
                    var sb = new StringBuilder();
                    foreach (var k in batch)
                        if (k.Key == ConsoleKey.Enter) sb.Append('\n');
                        else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
                    _editor.InsertText(sb.ToString());
                    handled = true;
                }

                // Redraw on input, and ~5x/sec while thinking so the timer advances.
                var now = _clock.ElapsedMilliseconds;
                if (handled || (Volatile.Read(ref _thinkingStartTicks) != 0 && now - lastTick > 180))
                {
                    RedrawBox();
                    lastTick = now;
                }
            }
            catch { /* never let the reader die */ }
            finally { _readerGate.Release(); }

            if (!SafeKeyAvailable()) Thread.Sleep(12);
        }
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
    /// Run <paramref name="action"/> with sole ownership of the console: pause the key reader, lift
    /// the scroll region and drop below the box so Spectre can render + read keys normally, then
    /// restore the region and box afterwards.
    /// </summary>
    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _readerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_render)
            {
                Console.Write("\x1b[r");                       // lift scroll region
                Console.Write($"\x1b[{_rows};1H\x1b[0m\r\n");  // move below the box
                _lastBoxHeight = -1;                            // force region re-apply on redraw
            }
            return await action().ConfigureAwait(false);
        }
        finally
        {
            lock (_render)
            {
                ApplyScrollRegion(force: true);
                Console.Write($"\x1b[{_rows - BoxHeight()};1H");
                RedrawBoxLocked();
            }
            _readerGate.Release();
        }
    }

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
                if ((k.Modifiers & ConsoleModifiers.Alt) != 0) { _editor.Newline(); return; }
                var text = _editor.Text;
                if (text.EndsWith("\\", StringComparison.Ordinal))
                {
                    _editor.Backspace();     // drop the trailing backslash
                    _editor.Newline();
                    return;
                }
                SubmitOrExit(text);
                return;
            case ConsoleKey.Backspace: _editor.Backspace(); return;
            case ConsoleKey.Delete: _editor.Delete(); return;
            case ConsoleKey.LeftArrow: _editor.Left(); return;
            case ConsoleKey.RightArrow: _editor.Right(); return;
            case ConsoleKey.UpArrow: _editor.Up(); return;
            case ConsoleKey.DownArrow: _editor.Down(); return;
            case ConsoleKey.Home: _editor.Home(); return;
            case ConsoleKey.End: _editor.End(); return;
            case ConsoleKey.Escape: return;
        }

        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            _editor.InsertChar(k.KeyChar);
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

    private void EmitOutputLine(string line)
    {
        lock (_render)
        {
            if (!_started) { Console.WriteLine(line); return; }
            RefreshDims();
            var outRow = _rows - BoxHeight();
            // Write the line on the last scrollable row, then scroll the region up by one.
            Console.Write($"\x1b[{outRow};1H\x1b[2K");
            Console.Write(Clip(line, _cols));
            Console.Write($"\x1b[{outRow};1H\x1b[1S"); // scroll region up 1
            RedrawBoxLocked();
        }
    }

    private void EchoQueuedLine(string text) =>
        EmitOutputLine($"{Cyan}⏳ queued:{Reset} {Mute}{Clip(text.Replace('\n', ' '), 72)}{Reset}");

    private void RedrawBox()
    {
        lock (_render) { if (_started) RedrawBoxLocked(); }
    }

    // Holds _render.
    private void RedrawBoxLocked()
    {
        RefreshDims();
        ApplyScrollRegion(force: false);

        var boxH = BoxHeight();
        var top = _rows - boxH + 1;           // first box row (1-based)
        var lines = _editor.Lines;
        var inputRows = Math.Clamp(lines.Count, 1, MaxInputRows);

        // Status line.
        WriteRow(top, StatusText());

        // Input lines (first prefixed "> ", continuations "  "). Show a window if there are more
        // lines than fit, keeping the cursor row visible.
        var firstLine = Math.Max(0, Math.Min(_editor.CursorRow - (inputRows - 1), lines.Count - inputRows));
        for (var i = 0; i < inputRows; i++)
        {
            var li = firstLine + i;
            var prefix = li == 0 ? $"{Cyan}>{Reset} " : "  ";
            var content = li < lines.Count ? lines[li] : "";
            WriteRow(top + 1 + i, prefix + Clip(content, _cols - 3));
        }

        // Footer.
        WriteRow(top + 1 + inputRows, FooterText());

        // Park the visible cursor at the editor position inside the input area.
        var curScreenRow = top + 1 + (_editor.CursorRow - firstLine);
        var curCol = 2 /* "> " */ + Math.Min(_editor.CursorCol, _cols - 4) + 1; // 1-based
        Console.Write($"\x1b[{curScreenRow};{curCol}H");
    }

    private string StatusText()
    {
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
        var mode = _bypassPermissions
            ? $"{Red}⚠ bypass permissions ON{Reset}"
            : $"{Mute}permissions: ask{Reset}";
        return $"{mode}  {Mute}·  / commands · Ctrl+C interrupt/exit{Reset}";
    }

    // Holds _render.
    private void WriteRow(int row, string content)
    {
        if (row < 1 || row > _rows) return;
        Console.Write($"\x1b[{row};1H\x1b[2K"); // go to row, clear it
        Console.Write(content);
    }

    private int BoxHeight()
    {
        var inputRows = Math.Clamp(_editor.Lines.Count, 1, MaxInputRows);
        return 1 /*status*/ + inputRows + 1 /*footer*/;
    }

    // Holds _render.
    private void ApplyScrollRegion(bool force)
    {
        var boxH = BoxHeight();
        if (!force && boxH == _lastBoxHeight) return;
        _lastBoxHeight = boxH;
        // Reserve the bottom box; output scrolls in rows 1..(rows-boxH).
        Console.Write($"\x1b[1;{Math.Max(1, _rows - boxH)}r");
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
        // Strip embedded newlines for single-row rendering; truncate to width.
        s = s.Replace("\r", "").Replace("\n", "⏎");
        return s.Length <= max ? s : s[..max];
    }

    private static bool SafeKeyAvailable()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _reader?.Wait(TimeSpan.FromMilliseconds(400)); } catch { }
        _cts?.Dispose();
        if (_started)
        {
            try
            {
                lock (_render)
                {
                    Console.Write("\x1b[r");              // reset scroll region
                    Console.Write($"\x1b[{_rows};1H\x1b[0m\n"); // cursor to bottom
                }
            }
            catch { }
        }
    }
}
