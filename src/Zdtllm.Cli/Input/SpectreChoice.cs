using Spectre.Console;
using Zdtllm.Core.Repl;
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

    /// <summary>
    /// How many choices fit on one page of a Spectre prompt.
    ///
    /// Spectre sizes a page in CHOICES, but every converter here renders a choice on TWO rows
    /// (name, then an indented description). A page of 15 therefore costs 30 rows plus the title
    /// and the "move up and down to reveal more choices" hint — over 30 rows total, which is taller
    /// than a default terminal. The prompt then paints past the bottom, scrolling the conversation
    /// (and the TUI's input box) off screen on every keystroke. Budget from the real window height
    /// instead. Spectre rejects a page size below 3, so that is the floor even on a tiny terminal.
    /// </summary>
    private static int PageSizeFor(int choiceCount, int rowsPerChoice = 2, int chrome = 6)
    {
        int height;
        try { height = Console.WindowHeight; }
        catch { height = 24; }                       // redirected/unknown: assume the classic 80x24
        if (height < 8) height = 24;

        var budget = (height - chrome) / rowsPerChoice;
        return Math.Clamp(choiceCount, 3, Math.Max(3, budget));
    }

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
        var pageSize = PageSizeFor(choices.Count);

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

    private static readonly SlashCommandInfo SlashCancelSentinel =
        new("(type it yourself)", "close this menu and type the command by hand");

    /// <summary>
    /// The <c>/</c>-command autocomplete picker: a searchable list of slash commands. Returns the
    /// chosen <c>/name</c>, or null if the user cancelled or picked the "type it yourself" escape
    /// hatch. The caller owns the console (pausing any background reader) before calling.
    /// </summary>
    public static async Task<string?> SelectSlashCommandAsync(
        IAnsiConsole console, IReadOnlyList<SlashCommandInfo> commands, CancellationToken ct)
    {
        var choices = commands.Append(SlashCancelSentinel).ToList();
        var prompt = new SelectionPrompt<SlashCommandInfo>()
            .Title($"[bold {Cyan}]/ commands[/]  [{Mute}](type to filter · ↑/↓ · Enter to pick)[/]")
            .PageSize(PageSizeFor(choices.Count))
            .HighlightStyle(new Style(new Color(0x1B, 0xEA, 0xCD)))
            .UseConverter(FormatSlash)
            .EnableSearch()
            .AddChoices(choices);
        try
        {
            var chosen = await prompt.ShowAsync(console, ct).ConfigureAwait(false);
            return ReferenceEquals(chosen, SlashCancelSentinel) ? null : chosen.Name;
        }
        catch (OperationCanceledException) { return null; }
    }

    private static string FormatSlash(SlashCommandInfo c) =>
        ReferenceEquals(c, SlashCancelSentinel)
            ? $"[{Mute}]{SearchSafe(c.Name)}[/]"
            : $"[bold {Cyan}]{SearchSafe(c.Name)}[/]\n    [{Mute}]{SearchSafe(c.Description)}[/]";

    /// <summary>
    /// Escape user text for markup AND swap square brackets for angle brackets. This is the
    /// slash picker's converter, and the picker has <c>EnableSearch()</c>. <see cref="Markup.Escape"/>
    /// alone protects only the FIRST paint: Spectre 0.49's SelectionPrompt search highlighter
    /// re-parses each item's plain (un-escaped) display text as markup to underline the match, so a
    /// literal <c>[path]</c> in a description (e.g. "/export [path]") is read as a style token and
    /// throws <c>Could not find color or style 'path'</c> the moment you type to filter. The picker
    /// then dies mid-render, the keystroke is swallowed, and the rest of what you type lands in the
    /// message box — a slash command gets sent as a message. Removing '[' from the VISIBLE text is
    /// what makes the search path safe; '&lt;name&gt;' also matches the placeholder style already used by
    /// "/workflow &lt;name&gt; key=value".
    /// </summary>
    private static string SearchSafe(string s) => Markup.Escape(s.Replace('[', '<').Replace(']', '>'));

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
