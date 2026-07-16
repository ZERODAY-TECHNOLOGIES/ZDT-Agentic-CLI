using Spectre.Console;
using Zdtllm.Tools;

namespace Zdtllm.Cli.Input;

/// <summary>
/// The Spectre-backed arrow-key chooser shared by the two interactive prompters
/// (<see cref="ConsoleInput"/> and the bottom-input TUI). Renders each option with its description
/// on the line below, always offers a "type your own answer" escape hatch when
/// <paramref name="allowFreeText"/> is set, and supports single- or multi-select. The caller is
/// responsible for owning the console (pausing any background key reader) before calling.
/// </summary>
internal static class SpectreChoice
{
    private const string Cyan = "#1BEACD";
    private const string Mute = "#687B89";

    private static readonly PromptChoice FreeTextChoice =
        new("✎ Something else…", "Type your own answer");

    public static async Task<IReadOnlyList<string>> SelectAsync(
        IAnsiConsole console,
        string question,
        string? header,
        IReadOnlyList<PromptChoice> options,
        bool multiSelect,
        bool allowFreeText,
        CancellationToken ct)
    {
        var title = BuildTitle(question, header);
        var choices = allowFreeText ? options.Append(FreeTextChoice).ToList() : options.ToList();
        var pageSize = Math.Clamp(choices.Count + 1, 3, 16);

        if (multiSelect)
        {
            var prompt = new MultiSelectionPrompt<PromptChoice>()
                .Title(title)
                .PageSize(pageSize)
                .NotRequired()
                .HighlightStyle(new Style(new Color(0x1B, 0xEA, 0xCD)))
                .UseConverter(FormatChoice)
                .AddChoices(choices);
            prompt.InstructionsText = $"[{Mute}](space to toggle, enter to confirm)[/]";
            var chosen = await prompt.ShowAsync(console, ct).ConfigureAwait(false);

            var result = new List<string>();
            foreach (var c in chosen)
            {
                if (ReferenceEquals(c, FreeTextChoice))
                {
                    var custom = await ReadFreeTextAsync(console, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(custom)) result.Add(custom.Trim());
                }
                else result.Add(c.Label);
            }
            return result;
        }
        else
        {
            var prompt = new SelectionPrompt<PromptChoice>()
                .Title(title)
                .PageSize(pageSize)
                .HighlightStyle(new Style(new Color(0x1B, 0xEA, 0xCD)))
                .UseConverter(FormatChoice)
                .AddChoices(choices);
            var chosen = await prompt.ShowAsync(console, ct).ConfigureAwait(false);

            if (ReferenceEquals(chosen, FreeTextChoice))
            {
                var custom = await ReadFreeTextAsync(console, ct).ConfigureAwait(false);
                return new[] { string.IsNullOrWhiteSpace(custom) ? "(no answer)" : custom.Trim() };
            }
            return new[] { chosen.Label };
        }
    }

    private static async Task<string> ReadFreeTextAsync(IAnsiConsole console, CancellationToken ct) =>
        await new TextPrompt<string>($"[{Cyan}]Your answer:[/]").AllowEmpty().ShowAsync(console, ct)
            .ConfigureAwait(false);

    private static string BuildTitle(string question, string? header)
    {
        var q = $"[bold {Cyan}]{Markup.Escape(question)}[/]";
        return string.IsNullOrWhiteSpace(header) ? q : $"[{Mute}]{Markup.Escape(header!)}[/]\n{q}";
    }

    private static string FormatChoice(PromptChoice c) =>
        string.IsNullOrWhiteSpace(c.Description)
            ? Markup.Escape(c.Label)
            : $"{Markup.Escape(c.Label)}\n    [{Mute}]{Markup.Escape(c.Description!)}[/]";
}
