using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Zdtllm.Core;

namespace Zdtllm.Cli;

/// <summary>
/// Brand-aware terminal output. The palette is lifted directly from zer0day.ro
/// (cyan #1BEACD + gold #E5D936 on the dark navy #132431, with muted blue-grays
/// for secondary text and red #ef4444 for errors). The wordmark visually splits
/// "zer0" (cyan) from "day" (gold) the same way the live logo's central "0"
/// gradient transitions teal → yellow.
/// </summary>
internal static class Branding
{
    // zer0day.ro brand palette
    public static readonly Color BrandCyan   = new(0x1B, 0xEA, 0xCD);
    public static readonly Color BrandGold   = new(0xE5, 0xD9, 0x36);
    public static readonly Color BrandRed    = new(0xEF, 0x44, 0x44);
    public static readonly Color BodyText    = new(0xE8, 0xED, 0xF2);
    public static readonly Color DimText     = new(0xAA, 0xB9, 0xC8);
    public static readonly Color MutedText   = new(0x68, 0x7B, 0x89);
    public static readonly Color BorderTint  = new(0x36, 0x4A, 0x5E);

    public const string Url = "https://zer0day.ro";

    /// <summary>
    /// Print the startup banner: figlet-art "zer0day" wordmark with the
    /// cyan/gold split, then a metadata strip showing the project tagline,
    /// the active model + mode + session id, and the brand URL. Wrapped in a
    /// rounded panel tinted with the brand mid-tone.
    /// </summary>
    public static void PrintStartupBanner(
        IAnsiConsole console,
        string version,
        string model,
        ToolCallingMode mode,
        string sessionDisplay)
    {
        var wordmark = BuildSplitWordmark();

        var subtitle = new Markup(
            $"[bold {Hex(BrandCyan)}]zdtllmcli[/] " +
            $"[{Hex(MutedText)}]· v{version} ·[/] " +
            $"[italic {Hex(DimText)}]CLI LLM Agent, backed by LiteLLM[/]");

        var meta = new Markup(
            $"[{Hex(MutedText)}]model[/] [bold {Hex(BrandGold)}]{Markup.Escape(model)}[/]   " +
            $"[{Hex(MutedText)}]mode[/] [{Hex(DimText)}]{mode.ToString().ToLowerInvariant()}[/]   " +
            $"[{Hex(MutedText)}]session[/] [{Hex(DimText)}]{Markup.Escape(sessionDisplay)}[/]");

        var url = new Markup($"[link={Url} {Hex(BrandCyan)}]{Url}[/]");

        var stack = new Rows(wordmark, new Markup(string.Empty), subtitle, meta, url);

        var panel = new Panel(stack)
            .Border(BoxBorder.Rounded)
            .BorderColor(BorderTint)
            .Padding(2, 1, 2, 1);

        console.Write(panel);

        console.MarkupLine(
            $"[{Hex(MutedText)}]Type [/]" +
            $"[bold {Hex(BodyText)}]/help[/]" +
            $"[{Hex(MutedText)}] for commands, [/]" +
            $"[bold {Hex(BodyText)}]/exit[/]" +
            $"[{Hex(MutedText)}] to quit. Ctrl+D / EOF also exits.[/]");
        console.WriteLine();
    }

    /// <summary>
    /// Compact version-only banner for `zdt --version`. One liner with the
    /// brand-coloured wordmark and the URL.
    /// </summary>
    public static void PrintVersion(IAnsiConsole console, string version)
    {
        console.MarkupLine(
            $"[bold {Hex(BrandCyan)}]zer0[/][bold {Hex(BrandGold)}]day[/] " +
            $"[bold {Hex(BodyText)}]zdtllmcli[/] " +
            $"[{Hex(MutedText)}]v{version}[/]   " +
            $"[link={Url} {Hex(BrandCyan)}]{Url}[/]");
    }

    public static string Hex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Render "zer0day" as a single tight figlet block whose middle "0" glyph is
    /// vertically halved — the left half painted cyan, the right half gold —
    /// matching the live zer0day.ro logo's gradient pivot. We render once with
    /// no colour to grab the raw glyph rows, then re-emit each row as Spectre
    /// markup, splitting at the column that sits in the middle of the "0".
    /// </summary>
    private static IRenderable BuildSplitWordmark()
    {
        var rows = CaptureFigletRows("zer0day");
        var splitColumn = (FigletWidth("zer") + FigletWidth("zer0")) / 2;

        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var leftLen = Math.Min(splitColumn, row.Length);
            var left = row[..leftLen];
            var right = row.Length > leftLen ? row[leftLen..] : string.Empty;

            sb.Append($"[{Hex(BrandCyan)}]");
            sb.Append(Markup.Escape(left));
            sb.Append("[/]");
            if (right.Length > 0)
            {
                sb.Append($"[{Hex(BrandGold)}]");
                sb.Append(Markup.Escape(right.TrimEnd()));
                sb.Append("[/]");
            }
            if (i < rows.Count - 1) sb.Append('\n');
        }
        return new Markup(sb.ToString());
    }

    /// <summary>
    /// Capture the colourless figlet output for <paramref name="text"/> and
    /// return the rows. Trailing all-whitespace rows are dropped, but rows that
    /// are visually blank yet sit between glyph rows are kept (some characters
    /// have empty leading rows in the standard font, and removing them would
    /// misalign the wordmark).
    /// </summary>
    private static IReadOnlyList<string> CaptureFigletRows(string text)
    {
        var sw = new StringWriter();
        var capture = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw),
            Interactive = InteractionSupport.No,
        });
        capture.Profile.Width = 500;
        capture.Write(new FigletText(text).LeftJustified());

        var rawRows = sw.ToString().Split('\n')
            .Select(r => r.TrimEnd('\r'))
            .ToList();

        // Drop trailing all-empty rows but keep mid-glyph blank rows.
        var lastNonEmpty = rawRows.FindLastIndex(r => r.TrimEnd().Length > 0);
        if (lastNonEmpty < 0) return Array.Empty<string>();
        return rawRows.Take(lastNonEmpty + 1).ToList();
    }

    private static int FigletWidth(string text)
    {
        var rows = CaptureFigletRows(text);
        return rows.Count == 0 ? 0 : rows.Max(r => r.TrimEnd().Length);
    }
}
