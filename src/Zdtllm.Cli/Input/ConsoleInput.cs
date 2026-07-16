using System.Text;
using Spectre.Console;
using Zdtllm.Core;
using Zdtllm.Core.Repl;
using Zdtllm.Tools;

namespace Zdtllm.Cli.Input;

/// <summary>
/// The single owner of console input for an interactive TTY session. It fills three roles that all
/// need the keyboard and must never contend for it (they run at disjoint times, serialised through
/// one console lock):
///
/// <list type="number">
/// <item><b>Idle line editing</b> (<see cref="IReplInputSource"/>): a poll-based line editor that
/// draws its own prompt and — the point of it — supports multi-line <b>paste without submitting</b>
/// (internal newlines stay literal; only a deliberate Enter submits) and <b>drag-and-drop</b> of
/// files (the dropped path is cleaned up and inserted). Large pastes collapse to a
/// <c>[pasted N lines]</c> chip. Full editing: cursor moves, Home/End, Backspace/Delete,
/// Ctrl+A/E/U/K, Ctrl+D to exit on an empty line.</item>
/// <item><b>Queue capture</b> (<see cref="ITurnInputCapture"/>): while a turn runs, keystrokes are
/// collected into the shared queue so the user can type ahead.</item>
/// <item><b>Interactive selection</b> (<see cref="IInteractivePrompter"/>): AskUserQuestion /
/// ExitPlanMode arrow-key pickers, driven through Spectre while capture is paused.</item>
/// </list>
///
/// Only built for an ANSI-capable interactive terminal; everything else keeps the classic
/// <c>Console.ReadLine</c> path.
/// </summary>
public sealed class ConsoleInput : IReplInputSource, ITurnInputCapture, IInteractivePrompter, ITypeAheadStatus, Zdtllm.Core.AgentFleet.IConsoleExclusive, IDisposable
{
    private const int PollMs = 6;
    private const string PromptAnsi = "\x1b[38;2;27;234;205m> \x1b[0m"; // brand cyan "> "
    private const int PromptVisibleLen = 2;

    private static readonly Color BrandCyan = new(0x1B, 0xEA, 0xCD);
    private static readonly Color MutedText = new(0x68, 0x7B, 0x89);

    private readonly IUserInputQueue _queue;
    private readonly IAnsiConsole _console;
    private readonly IReadOnlyList<SlashCommandInfo> _slashCommands;
    private readonly SemaphoreSlim _consoleLock = new(1, 1);

    // Idle line-editor state.
    private readonly LineEditorState _state = new();
    private int _lastRenderVisibleLen;

    // Set once the model is resolved (see Program). When true, dropped image files are attached as
    // vision content parts; when false they're inserted as a plain path (the model has no vision).
    public bool VisionCapable { get; set; }

    // Image attachments from the line that was just submitted, handed to the REPL via
    // TakePendingImages(). Captured at submit time from the buffer, so a chip the user backspaced
    // away is correctly excluded.
    private IReadOnlyList<string> _pendingImages = Array.Empty<string>();

    // Queue-capture state.
    private readonly StringBuilder _captureBuf = new();
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    public ConsoleInput(
        IUserInputQueue queue,
        IAnsiConsole console,
        IReadOnlyList<SlashCommandInfo>? slashCommands = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(console);
        _queue = queue;
        _console = console;
        _slashCommands = slashCommands ?? Array.Empty<SlashCommandInfo>();
    }

    public bool IsAvailable => true;

    // ================= type-ahead readout (ITypeAheadStatus) =================
    // Exposed so the AgentLoop's "thinking" spinner can show what the user is typing mid-turn and
    // how many messages are queued — without that, silent capture reads as a frozen terminal.

    public string CurrentInput
    {
        get { lock (_captureBuf) return _captureBuf.ToString(); }
    }

    public int QueuedCount => _queue.Count;

    public IReadOnlyList<string> TakePendingImages()
    {
        var imgs = _pendingImages;
        _pendingImages = Array.Empty<string>();
        return imgs;
    }

    /// <summary>
    /// Take exclusive ownership of the console (pausing the queue-capture reader) for as long as the
    /// returned handle is held. Used by the interactive agent fleet view so its key navigation
    /// doesn't fight the capture loop. Blocks until the capture reader yields (it does so every poll
    /// tick, so this returns promptly).
    /// </summary>
    public IDisposable EnterExclusive()
    {
        _consoleLock.Wait();
        return new Releaser(_consoleLock);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() { _sem?.Release(); _sem = null; }
    }

    // ================= idle line editor (IReplInputSource) =================

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _state.Clear();
        _lastRenderVisibleLen = 0;
        await _consoleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnableBracketedPaste();
            Render();
            while (true)
            {
                if (ct.IsCancellationRequested) return null;
                if (!SafeKeyAvailable())
                {
                    try { await Task.Delay(PollMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }
                    continue;
                }

                var batch = DrainKeys();
                var outcome = ProcessBatch(batch);
                if (outcome == Outcome.Submit)
                {
                    // Snapshot image attachments from the buffer NOW (chips backspaced away are
                    // gone) so TakePendingImages() hands the REPL exactly what's still attached.
                    _pendingImages = _state.Images();
                    Console.Write(Environment.NewLine);
                    return _state.Resolve();
                }
                if (outcome == Outcome.Eof)
                {
                    Console.Write(Environment.NewLine);
                    return null;
                }
                if (outcome == Outcome.OpenCommandMenu)
                {
                    await OpenCommandMenuAsync(ct).ConfigureAwait(false);
                    _lastRenderVisibleLen = 0; // the picker drew its own UI — redraw the prompt clean
                }
                Render();
            }
        }
        finally
        {
            DisableBracketedPaste();
            _consoleLock.Release();
        }
    }

    private enum Outcome { Continue, Submit, Eof, OpenCommandMenu }

    private List<ConsoleKeyInfo> DrainKeys()
    {
        var keys = new List<ConsoleKeyInfo>();
        while (SafeKeyAvailable())
        {
            keys.Add(Console.ReadKey(intercept: true));
            if (keys.Count >= 8192) break; // grab the rest on the next tick
        }
        return keys;
    }

    private Outcome ProcessBatch(List<ConsoleKeyInfo> keys)
    {
        if (keys.Count == 0) return Outcome.Continue;
        if (keys.Count == 1) return ProcessSingleKey(keys[0]);

        // A multi-key burst arriving inside one poll tick is a paste (or drag-and-drop). Rebuild
        // its text, drop bracketed-paste markers, and insert it — internal newlines never submit.
        var text = InputText.StripBracketedPasteMarkers(InputText.ReconstructBurst(keys));
        if (text.Length == 0)
        {
            foreach (var k in keys)
            {
                var o = ProcessSingleKey(k);
                if (o != Outcome.Continue) return o;
            }
            return Outcome.Continue;
        }

        InsertPasted(text);
        return Outcome.Continue;
    }

    private void InsertPasted(string text)
    {
        if (text.IndexOf('\n') < 0)
        {
            // Single line: a dropped image file (on a vision model) attaches as an image chip;
            // anything else is inserted as clean path / text.
            var norm = InputText.NormalizeDroppedPath(text);
            if (VisionCapable && InputText.TryLoadImageDataUri(norm, out var uri, out var name))
                _state.InsertImage(uri, name);
            else
                _state.InsertText(norm);
        }
        else
        {
            // Multiple files dropped at once arrive newline-separated. If they're all images (and
            // the model has vision), attach each; otherwise collapse to a compact paste chip.
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (VisionCapable && lines.Length > 0 && lines.All(InputText.IsImagePath))
            {
                foreach (var line in lines)
                    if (InputText.TryLoadImageDataUri(line, out var uri, out var name))
                        _state.InsertImage(uri, name);
            }
            else
            {
                _state.InsertPaste(text.TrimEnd('\n', '\r'));
            }
        }
    }

    private Outcome ProcessSingleKey(ConsoleKeyInfo k)
    {
        if ((k.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (k.Key)
            {
                case ConsoleKey.D:
                    if (_state.IsEmpty) return Outcome.Eof;
                    _state.Delete();
                    return Outcome.Continue;
                case ConsoleKey.A: _state.Home(); return Outcome.Continue;
                case ConsoleKey.E: _state.End(); return Outcome.Continue;
                case ConsoleKey.U: _state.KillToStart(); return Outcome.Continue;
                case ConsoleKey.K: _state.KillToEnd(); return Outcome.Continue;
            }
        }

        switch (k.Key)
        {
            case ConsoleKey.Enter: return Outcome.Submit;
            case ConsoleKey.Backspace: _state.Backspace(); return Outcome.Continue;
            case ConsoleKey.Delete: _state.Delete(); return Outcome.Continue;
            case ConsoleKey.LeftArrow: _state.MoveLeft(); return Outcome.Continue;
            case ConsoleKey.RightArrow: _state.MoveRight(); return Outcome.Continue;
            case ConsoleKey.Home: _state.Home(); return Outcome.Continue;
            case ConsoleKey.End: _state.End(); return Outcome.Continue;
            case ConsoleKey.Escape:
            case ConsoleKey.UpArrow:
            case ConsoleKey.DownArrow:
                return Outcome.Continue; // ignored (no history yet)
        }

        // Typing "/" on an empty prompt opens the slash-command autocomplete picker instead of
        // inserting the "/" literally — the picker supplies the full "/command".
        if (k.KeyChar == '/' && _state.IsEmpty && _slashCommands.Count > 0)
            return Outcome.OpenCommandMenu;

        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
            _state.InsertChar(k.KeyChar);
        return Outcome.Continue;
    }

    // ================= slash-command autocomplete =================

    private async Task OpenCommandMenuAsync(CancellationToken ct)
    {
        // Wipe the prompt line before the picker renders over it.
        Console.Write("\r" + new string(' ', SafeWindowWidth() - 1) + "\r");

        // Shared with the TUI so both interfaces show the same picker (see SpectreChoice).
        var chosen = await SpectreChoice.SelectSlashCommandAsync(_console, _slashCommands, ct).ConfigureAwait(false);
        if (chosen is not null)
            _state.InsertText(chosen + " ");  // fill the line; user adds args or hits Enter
        else
            _state.InsertChar('/');           // cancelled → keep the slash for manual typing
    }

    private void Render()
    {
        var display = _state.Display();
        var cursorCol = _state.CursorDisplayColumn;
        var width = SafeWindowWidth();
        var avail = Math.Max(8, width - PromptVisibleLen - 1);

        // Horizontal scroll: keep the cursor inside a window that never exceeds the line width,
        // so `\r` + reprint stays on one physical row (no wrap math).
        var start = 0;
        if (cursorCol > avail) start = cursorCol - avail;
        if (start > display.Length) start = Math.Max(0, display.Length - avail);
        var len = Math.Min(avail, Math.Max(0, display.Length - start));
        var windowed = display.Substring(start, len);
        var cursorInWindow = Math.Clamp(cursorCol - start, 0, windowed.Length);

        var sb = new StringBuilder();
        sb.Append('\r').Append(PromptAnsi).Append(windowed);
        var visible = PromptVisibleLen + windowed.Length;
        if (visible < _lastRenderVisibleLen)
            sb.Append(new string(' ', _lastRenderVisibleLen - visible)); // erase leftovers
        // Reposition the cursor by reprinting the prompt + the prefix up to the cursor.
        sb.Append('\r').Append(PromptAnsi).Append(windowed.AsSpan(0, cursorInWindow));
        _lastRenderVisibleLen = visible;

        Console.Write(sb.ToString());
    }

    private static void EnableBracketedPaste() { try { Console.Write("\x1b[?2004h"); } catch { /* ignore */ } }
    private static void DisableBracketedPaste() { try { Console.Write("\x1b[?2004l"); } catch { /* ignore */ } }

    private static int SafeWindowWidth()
    {
        try { var w = Console.WindowWidth; return w > 0 ? w : 80; }
        catch { return 80; }
    }

    // ================= queue capture (ITurnInputCapture) =================

    public void BeginCapture()
    {
        TerminalStatus.Working();   // taskbar/tab: "working"
        if (_captureTask is not null) return;
        lock (_captureBuf) _captureBuf.Clear();
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;
        _captureTask = Task.Run(() => CaptureLoopAsync(token));
    }

    public async Task EndCaptureAsync()
    {
        TerminalStatus.Idle();      // taskbar/tab: "ready" + flash
        var cts = _captureCts;
        var task = _captureTask;
        _captureCts = null;
        _captureTask = null;
        if (cts is null) return;
        try
        {
            cts.Cancel();
            if (task is not null) await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* capture is best-effort */ }
        finally
        {
            cts.Dispose();
            lock (_captureBuf) _captureBuf.Clear();
        }
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _consoleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // A transient console hiccup while a turn's live spinner is rendering must not kill
                // capture for the rest of the turn — swallow and retry on the next tick.
                while (!ct.IsCancellationRequested && SafeKeyAvailable())
                    HandleCaptureKey(Console.ReadKey(intercept: true));
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* retry next tick */ }
            finally
            {
                _consoleLock.Release();
            }

            try { await Task.Delay(25, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void HandleCaptureKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                string line;
                lock (_captureBuf) { line = _captureBuf.ToString(); _captureBuf.Clear(); }
                _queue.Enqueue(line);
                break;
            case ConsoleKey.Backspace:
                lock (_captureBuf) if (_captureBuf.Length > 0) _captureBuf.Remove(_captureBuf.Length - 1, 1);
                break;
            case ConsoleKey.Escape:
                lock (_captureBuf) _captureBuf.Clear();
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                    lock (_captureBuf) _captureBuf.Append(key.KeyChar);
                break;
        }
    }

    private static bool SafeKeyAvailable()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }

    // ================= interactive selection (IInteractivePrompter) =================

    public async Task<IReadOnlyList<string>> SelectAsync(
        string question, string? header, IReadOnlyList<PromptChoice> options,
        bool multiSelect, bool allowFreeText, CancellationToken ct)
    {
        // Own the console (pausing the capture reader) so the Spectre prompt has sole key access.
        await _consoleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SpectreChoice.SelectAsync(_console, question, header, options, multiSelect, allowFreeText, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _consoleLock.Release();
        }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public void Dispose()
    {
        try { _captureCts?.Cancel(); } catch { /* ignore */ }
        _captureCts?.Dispose();
        _consoleLock.Dispose();
    }
}
