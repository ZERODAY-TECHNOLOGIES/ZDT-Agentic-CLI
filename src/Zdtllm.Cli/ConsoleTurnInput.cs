using System.Text;
using Spectre.Console;
using Zdtllm.Core;
using Zdtllm.Tools;

namespace Zdtllm.Cli;

/// <summary>
/// The single owner of console input while a turn is in flight. It serves two jobs that both
/// need the keyboard and must never fight over it:
///
/// <list type="number">
/// <item><b>Queue capture</b> (<see cref="ITurnInputCapture"/>): a poll-based reader that
/// collects keystrokes the user types while the model is working and enqueues each completed
/// line into the shared <see cref="IUserInputQueue"/>. Poll-based (<c>Console.KeyAvailable</c>)
/// rather than a blocking read, so it can be paused/stopped instantly with no orphaned read.</item>
/// <item><b>Interactive selection</b> (<see cref="IInteractivePrompter"/>): when the model calls
/// AskUserQuestion mid-turn, it pauses the capture reader (via a shared console lock) and hands
/// the keyboard to a Spectre selection prompt, then resumes capture. One reader at a time — no
/// concurrent <c>ReadKey</c> loops.</item>
/// </list>
///
/// Only constructed for a real interactive TTY; print mode / redirected stdin never build one
/// (the queue feature stays off and AskUserQuestion falls back to <see cref="UnavailablePrompter"/>).
/// Idle input (the REPL prompt between turns) deliberately still goes through the normal
/// <c>Console.ReadLine</c> so the user keeps full native line editing; capture only runs during a
/// turn, and the two are never active at the same time.
/// </summary>
public sealed class ConsoleTurnInput : ITurnInputCapture, IInteractivePrompter, IDisposable
{
    private static readonly Color BrandCyan = new(0x1B, 0xEA, 0xCD);
    private static readonly Color MutedText = new(0x68, 0x7B, 0x89);

    private readonly IUserInputQueue _queue;
    private readonly IAnsiConsole _console;
    private readonly SemaphoreSlim _consoleLock = new(1, 1);
    private readonly StringBuilder _lineBuf = new();

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    public ConsoleTurnInput(IUserInputQueue queue, IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(console);
        _queue = queue;
        _console = console;
    }

    public bool IsAvailable => true;

    // ---- ITurnInputCapture ---------------------------------------------------------------

    public void BeginCapture()
    {
        if (_captureTask is not null) return; // already capturing
        lock (_lineBuf) _lineBuf.Clear();
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;
        _captureTask = Task.Run(() => CaptureLoopAsync(token));
    }

    public async Task EndCaptureAsync()
    {
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
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch { /* the reader is best-effort; never let it break the turn */ }
        finally
        {
            cts.Dispose();
            lock (_lineBuf) _lineBuf.Clear();
        }
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Hold the console lock only while actually draining available keys; release it
            // around the poll delay so SelectAsync can grab it to run a prompt. Keys are echoed
            // to nothing on purpose — echoing during the model's live status spinner would fight
            // it for the cursor. The user gets confirmation via the "picked up your queued
            // message" line the AgentLoop prints between tool rounds.
            await _consoleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (!ct.IsCancellationRequested && SafeKeyAvailable())
                {
                    var key = Console.ReadKey(intercept: true);
                    HandleKey(key);
                }
            }
            finally
            {
                _consoleLock.Release();
            }

            try { await Task.Delay(25, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                string line;
                lock (_lineBuf)
                {
                    line = _lineBuf.ToString();
                    _lineBuf.Clear();
                }
                _queue.Enqueue(line); // blank lines are ignored by the queue
                break;

            case ConsoleKey.Backspace:
                lock (_lineBuf)
                    if (_lineBuf.Length > 0) _lineBuf.Remove(_lineBuf.Length - 1, 1);
                break;

            case ConsoleKey.Escape:
                lock (_lineBuf) _lineBuf.Clear();
                break;

            default:
                if (!char.IsControl(key.KeyChar))
                    lock (_lineBuf) _lineBuf.Append(key.KeyChar);
                break;
        }
    }

    private static bool SafeKeyAvailable()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; } // stdin redirected — nothing to poll
    }

    // ---- IInteractivePrompter ------------------------------------------------------------

    public async Task<IReadOnlyList<string>> SelectAsync(
        string question,
        string? header,
        IReadOnlyList<PromptChoice> options,
        bool multiSelect,
        CancellationToken ct)
    {
        // Take the console lock so the capture reader can't consume the keystrokes meant for the
        // selection prompt — this is what "pauses" capture for the duration of the prompt.
        await _consoleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var title = BuildTitle(question, header);
            if (multiSelect)
            {
                var prompt = new MultiSelectionPrompt<PromptChoice>()
                    .Title(title)
                    .PageSize(Math.Clamp(options.Count + 1, 3, 12))
                    .NotRequired()
                    .HighlightStyle(new Style(BrandCyan))
                    .UseConverter(FormatChoice)
                    .AddChoices(options);
                prompt.InstructionsText =
                    $"[{Hex(MutedText)}](space to toggle, enter to confirm)[/]";

                var chosen = await prompt.ShowAsync(_console, ct).ConfigureAwait(false);
                return chosen.Select(c => c.Label).ToList();
            }
            else
            {
                var prompt = new SelectionPrompt<PromptChoice>()
                    .Title(title)
                    .PageSize(Math.Clamp(options.Count + 1, 3, 12))
                    .HighlightStyle(new Style(BrandCyan))
                    .UseConverter(FormatChoice)
                    .AddChoices(options);

                var chosen = await prompt.ShowAsync(_console, ct).ConfigureAwait(false);
                return new[] { chosen.Label };
            }
        }
        finally
        {
            _consoleLock.Release();
        }
    }

    private static string BuildTitle(string question, string? header)
    {
        var q = $"[bold {Hex(BrandCyan)}]{Markup.Escape(question)}[/]";
        return string.IsNullOrWhiteSpace(header)
            ? q
            : $"[{Hex(MutedText)}]{Markup.Escape(header!)}[/]\n{q}";
    }

    private static string FormatChoice(PromptChoice c) =>
        string.IsNullOrWhiteSpace(c.Description)
            ? Markup.Escape(c.Label)
            : $"{Markup.Escape(c.Label)}  [{Hex(MutedText)}]— {Markup.Escape(c.Description!)}[/]";

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public void Dispose()
    {
        try { _captureCts?.Cancel(); } catch { /* ignore */ }
        _captureCts?.Dispose();
        _consoleLock.Dispose();
    }
}
