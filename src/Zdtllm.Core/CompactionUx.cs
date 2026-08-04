using Spectre.Console;

namespace Zdtllm.Core;

/// <summary>
/// Runs a compaction operation while showing an animated "compacting…" indicator matched to the
/// active front-end, so manual <c>/compact</c> and mid-turn auto-compact look the same:
/// <list type="bullet">
///   <item>rich console → a Spectre status spinner ("⠋ Compacting conversation…"), same look as
///     the streaming spinner;</item>
///   <item>bottom-input TUI (no rich console) → the capture's own animated status row via
///     <see cref="ITurnInputCapture.BeginCompacting"/>;</item>
///   <item>print mode / tests → nothing, just await the work.</item>
/// </list>
/// The summarisation call blocks for seconds; without this the terminal looks frozen.
/// </summary>
internal static class CompactionUx
{
    private const string Label = "Compacting conversation…";
    // zdt brand cyan (#1BEACD), matching the streaming spinner in AgentLoop.
    private static readonly Style SpinnerStyle = new(new Color(0x1B, 0xEA, 0xCD));

    public static async Task<int> RunAsync(
        IAnsiConsole? rich, ITurnInputCapture? capture, Func<Task<int>> compact)
    {
        ArgumentNullException.ThrowIfNull(compact);

        if (rich is not null)
        {
            var result = 0;
            await rich.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(SpinnerStyle)
                .StartAsync(Label, async _ => result = await compact().ConfigureAwait(false))
                .ConfigureAwait(false);
            return result;
        }

        if (capture is not null)
        {
            using (capture.BeginCompacting())
                return await compact().ConfigureAwait(false);
        }

        return await compact().ConfigureAwait(false);
    }
}
