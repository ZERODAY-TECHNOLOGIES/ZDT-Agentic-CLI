using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Zdtllm.Core;

/// <summary>
/// Minimal markdown → Spectre.Console renderer. Covers what LLMs typically emit:
/// ATX headings (#..######), bold (**), italic (*/_), inline code (`),
/// code fences (```), pipe tables, ordered + unordered lists, blockquotes,
/// horizontal rules, and inline links. Anything we don't recognise falls through
/// as a plain paragraph with inline markup applied. Output is brand-tinted —
/// cyan for code/headings/links, gold for h2, muted/dim for secondary chrome.
///
/// Not a full CommonMark parser. We optimise for "looks right on the
/// 80% of model output we see" rather than spec compliance.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly Color BrandCyan = new(0x1B, 0xEA, 0xCD);
    private static readonly Color BrandGold = new(0xE5, 0xD9, 0x36);
    private static readonly Color BodyText = new(0xE8, 0xED, 0xF2);
    private static readonly Color DimText = new(0xAA, 0xB9, 0xC8);
    private static readonly Color MuteText = new(0x68, 0x7B, 0x89);
    private static readonly Color BorderTint = new(0x36, 0x4A, 0x5E);

    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex BoldRegex();
    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")] private static partial Regex StarItalicRegex();
    [GeneratedRegex(@"(?<!_)_([^_\n]+?)_(?!_)")] private static partial Regex UnderscoreItalicRegex();
    [GeneratedRegex(@"`([^`]+?)`")] private static partial Regex InlineCodeRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex LinkRegex();
    [GeneratedRegex(@"^\s*([-*+])\s+(.+)$")] private static partial Regex BulletItemRegex();
    [GeneratedRegex(@"^\s*\d+[.)]\s+(.+)$")] private static partial Regex OrderedItemRegex();
    [GeneratedRegex(@"^\s*>\s?(.*)$")] private static partial Regex BlockquoteRegex();
    [GeneratedRegex(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$")] private static partial Regex HorizontalRuleRegex();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$")] private static partial Regex TableSeparatorRegex();

    public static IRenderable Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<IRenderable>();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                i = ConsumeCodeFence(lines, i, blocks);
                continue;
            }

            if (i + 1 < lines.Length && IsTableHeader(line, lines[i + 1]))
            {
                i = ConsumeTable(lines, i, blocks);
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                blocks.Add(new Rule().RuleStyle(new Style(BorderTint)));
                i++;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var (level, title) = ParseHeading(trimmed);
                if (level > 0)
                {
                    var color = level switch
                    {
                        1 => BrandCyan,
                        2 => BrandGold,
                        _ => DimText,
                    };
                    blocks.Add(new Markup($"[bold {Hex(color)}]{RenderInline(title)}[/]"));
                    i++;
                    continue;
                }
            }

            if (BulletItemRegex().IsMatch(line) || OrderedItemRegex().IsMatch(line))
            {
                i = ConsumeList(lines, i, blocks);
                continue;
            }

            if (BlockquoteRegex().IsMatch(line))
            {
                i = ConsumeBlockquote(lines, i, blocks);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                blocks.Add(Text.Empty);
                i++;
                continue;
            }

            blocks.Add(new Markup(RenderInline(line)));
            i++;
        }

        return new Rows(blocks);
    }

    private static int ConsumeCodeFence(string[] lines, int start, List<IRenderable> blocks)
    {
        // Skip opening fence (and grab language hint if present, just for color flavor)
        var fence = lines[start].TrimStart();
        var i = start + 1;
        var code = new StringBuilder();
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            if (code.Length > 0) code.Append('\n');
            code.Append(lines[i]);
            i++;
        }
        if (i < lines.Length) i++; // skip closing fence

        var content = code.ToString();
        var panel = new Panel(new Markup(Markup.Escape(content), new Style(BodyText)))
            .Border(BoxBorder.Rounded)
            .BorderColor(BorderTint)
            .Padding(1, 0);
        blocks.Add(panel);
        return i;
    }

    private static bool IsTableHeader(string headerLine, string nextLine) =>
        headerLine.TrimStart().StartsWith('|') &&
        TableSeparatorRegex().IsMatch(nextLine);

    private static int ConsumeTable(string[] lines, int start, List<IRenderable> blocks)
    {
        var headerCols = ParseTableRow(lines[start]);
        var i = start + 2; // skip header + separator
        var rows = new List<string[]>();
        while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
        {
            rows.Add(ParseTableRow(lines[i]));
            i++;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(BorderTint);
        foreach (var col in headerCols)
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(BrandCyan)}]{RenderInline(col)}[/]")));

        foreach (var row in rows)
        {
            var cells = new IRenderable[headerCols.Length];
            for (var c = 0; c < headerCols.Length; c++)
            {
                var raw = c < row.Length ? row[c] : string.Empty;
                cells[c] = new Markup(RenderInline(raw));
            }
            table.AddRow(cells);
        }
        blocks.Add(table);
        return i;
    }

    private static string[] ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static (int Level, string Title) ParseHeading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level == 0 || level > 6) return (0, string.Empty);
        if (level >= line.Length || line[level] != ' ') return (0, string.Empty);
        return (level, line[(level + 1)..].TrimEnd());
    }

    private static int ConsumeList(string[] lines, int start, List<IRenderable> blocks)
    {
        var i = start;
        while (i < lines.Length)
        {
            var line = lines[i];
            string? itemText = null;
            string bullet = "•";
            var bm = BulletItemRegex().Match(line);
            if (bm.Success) { itemText = bm.Groups[2].Value; bullet = "•"; }
            else
            {
                var om = OrderedItemRegex().Match(line);
                if (om.Success) { itemText = om.Groups[1].Value; bullet = "›"; }
            }

            if (itemText is null) break;

            var indent = line.Length - line.TrimStart().Length;
            var pad = new string(' ', Math.Min(indent + 2, 8));
            blocks.Add(new Markup($"{pad}[{Hex(MuteText)}]{bullet}[/] {RenderInline(itemText)}"));
            i++;
        }
        return i;
    }

    private static int ConsumeBlockquote(string[] lines, int start, List<IRenderable> blocks)
    {
        var i = start;
        var sb = new StringBuilder();
        while (i < lines.Length)
        {
            var m = BlockquoteRegex().Match(lines[i]);
            if (!m.Success) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(m.Groups[1].Value);
            i++;
        }
        var quote = new Markup($"[italic {Hex(DimText)}]{RenderInline(sb.ToString())}[/]");
        var panel = new Panel(quote)
            .Border(BoxBorder.None)
            .Padding(2, 0, 0, 0);
        blocks.Add(panel);
        return i;
    }

    /// <summary>
    /// Apply inline markdown decorations (bold, italic, code, links) to a single
    /// line and emit Spectre Markup syntax. The input is escaped first so any
    /// literal '[' / ']' the model produced doesn't get interpreted as a markup tag.
    /// </summary>
    internal static string RenderInline(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Step 1: escape Spectre's own markup chars in the raw text.
        var s = Markup.Escape(text);

        // Step 2: links — process before code so that `text` inside a link isn't
        // shadowed by inline-code rules.
        s = LinkRegex().Replace(s, m =>
        {
            var label = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            return $"[link={url} {Hex(BrandCyan)}]{label}[/]";
        });

        // Step 3: inline code — eats its content so bold/italic inside backticks stays literal.
        s = InlineCodeRegex().Replace(s, m =>
            $"[{Hex(BrandCyan)}]{m.Groups[1].Value}[/]");

        // Step 4: bold (run before italic so **foo** doesn't get caught by *foo*).
        s = BoldRegex().Replace(s, "[bold]$1[/]");

        // Step 5: italic via * and _.
        s = StarItalicRegex().Replace(s, "[italic]$1[/]");
        s = UnderscoreItalicRegex().Replace(s, "[italic]$1[/]");

        return s;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
