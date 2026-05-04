using Spectre.Console;
using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class MarkdownRendererTests
{
    /// <summary>
    /// Renders the given markdown into a Spectre console configured with no ANSI / no colors,
    /// so the resulting string holds the plain text the user would see — exactly what we want
    /// to grep for in assertions. The bracketed [color] markup is stripped by the renderer
    /// when colors are off; structural decoration (panel borders, table rules) survives.
    /// </summary>
    private static string Render(string markdown)
    {
        using var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw),
            // Wide enough that wrap doesn't fragment our test substrings.
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 200;
        console.Write(MarkdownRenderer.Render(markdown));
        return sw.ToString();
    }

    [Fact]
    public void Renders_h1_heading_text()
    {
        var output = Render("# Hello World");
        output.Should().Contain("Hello World");
    }

    [Fact]
    public void Renders_h2_through_h6_heading_text()
    {
        var output = Render("## Sub Heading\n\n### Tertiary\n\n###### Tiny");
        output.Should().Contain("Sub Heading");
        output.Should().Contain("Tertiary");
        output.Should().Contain("Tiny");
    }

    [Fact]
    public void Bold_inline_renders_visible_content_without_asterisks()
    {
        var output = Render("This is **important** text.");
        output.Should().Contain("important");
        // The literal asterisks must be consumed.
        output.Should().NotContain("**important**");
    }

    [Fact]
    public void Italic_inline_via_underscores_renders_visible_content()
    {
        var output = Render("Some _emphasised_ word.");
        output.Should().Contain("emphasised");
        output.Should().NotContain("_emphasised_");
    }

    [Fact]
    public void Inline_code_renders_content_without_backticks()
    {
        var output = Render("Run `git status` to check.");
        output.Should().Contain("git status");
        output.Should().NotContain("`git status`");
    }

    [Fact]
    public void Code_fence_renders_panelled_content()
    {
        var output = Render(
            "Look:\n" +
            "```python\n" +
            "print('hello')\n" +
            "```\n");
        output.Should().Contain("print('hello')");
    }

    [Fact]
    public void Bullet_list_renders_visible_items_with_marker()
    {
        var output = Render("- alpha\n- beta\n- gamma");
        output.Should().Contain("alpha");
        output.Should().Contain("beta");
        output.Should().Contain("gamma");
        output.Should().Contain("•");
    }

    [Fact]
    public void Ordered_list_renders_items_with_arrow_marker()
    {
        var output = Render("1. first\n2. second");
        output.Should().Contain("first");
        output.Should().Contain("second");
        output.Should().Contain("›");
    }

    [Fact]
    public void Pipe_table_renders_header_and_data_rows()
    {
        var output = Render(
            "| name | age |\n" +
            "|------|----:|\n" +
            "| alice | 30 |\n" +
            "| bob   | 25 |\n");
        output.Should().Contain("name");
        output.Should().Contain("age");
        output.Should().Contain("alice");
        output.Should().Contain("30");
        output.Should().Contain("bob");
        output.Should().Contain("25");
    }

    [Fact]
    public void Blockquote_renders_quoted_text()
    {
        var output = Render("> a wise quote\n> on two lines");
        output.Should().Contain("a wise quote");
        output.Should().Contain("on two lines");
    }

    [Fact]
    public void Horizontal_rule_renders_a_line()
    {
        var output = Render("intro\n\n---\n\noutro");
        output.Should().Contain("intro");
        output.Should().Contain("outro");
        // Spectre Rule renders box-drawing horizontal characters.
        (output.Contains('─') || output.Contains('-')).Should().BeTrue();
    }

    [Fact]
    public void Inline_link_renders_label_text()
    {
        var output = Render("Check [zer0day](https://zer0day.ro) for info.");
        output.Should().Contain("zer0day");
    }

    [Fact]
    public void Square_brackets_outside_links_are_escaped_not_interpreted_as_markup()
    {
        // Without escaping, Spectre would try to parse [bracketed] as a markup tag and crash.
        var act = () => Render("Some [bracketed] text that is not a link.");
        act.Should().NotThrow();
        var output = Render("Some [bracketed] text that is not a link.");
        output.Should().Contain("[bracketed]");
    }

    [Fact]
    public void Plain_paragraph_passes_through_as_text()
    {
        var output = Render("Just a normal sentence.");
        output.Should().Contain("Just a normal sentence.");
    }

    [Fact]
    public void Markdown_language_fence_unwraps_and_renders_inner_content_as_markdown()
    {
        // Qwen / Codestral / a few other models occasionally wrap their full response in
        // ```markdown ... ``` thinking it preserves formatting; the literal effect was the
        // opposite — our renderer used to wrap it in a code Panel and the table + headings
        // surfaced as raw '#' / '|' / '**' characters. The unwrap branch in ConsumeCodeFence
        // makes the markdown render normally inside the fence.
        var output = Render(
            "```markdown\n" +
            "# Findings\n" +
            "\n" +
            "| Issue | Severity |\n" +
            "|-------|----------|\n" +
            "| IDOR  | High     |\n" +
            "```\n");

        output.Should().Contain("Findings");
        output.Should().Contain("Issue");
        output.Should().Contain("Severity");
        output.Should().Contain("IDOR");
        output.Should().Contain("High");
        // The literal '#' heading marker MUST be consumed (would survive in a code-block panel).
        output.Should().NotContain("# Findings");
        // The literal '|---|---|' separator MUST be consumed by the table parser.
        output.Should().NotContain("|-------|");
    }

    [Fact]
    public void Md_language_fence_unwraps_same_as_markdown()
    {
        var output = Render(
            "```md\n" +
            "## Recap\n" +
            "**Bold** word.\n" +
            "```\n");

        output.Should().Contain("Recap");
        output.Should().Contain("Bold");
        output.Should().NotContain("## Recap");
        output.Should().NotContain("**Bold**");
    }

    [Fact]
    public void Markdown_fence_followed_by_more_markdown_renders_both_correctly()
    {
        // The exact shape that broke in the user's Qwen scan output: a markdown fence
        // wrapping a heading + table, then a closing fence, then more markdown OUTSIDE
        // the fence. Both halves must render visibly.
        var output = Render(
            "```markdown\n" +
            "# Vulnerability Assessment\n" +
            "\n" +
            "| Vuln | Sev |\n" +
            "|------|-----|\n" +
            "| IDOR | Crit |\n" +
            "```\n" +
            "\n" +
            "Recommendations\n" +
            "\n" +
            "1. Fix IDOR\n" +
            "2. Add rate limit\n");

        output.Should().Contain("Vulnerability Assessment");
        output.Should().Contain("IDOR");
        output.Should().Contain("Recommendations");
        output.Should().Contain("Fix IDOR");
        output.Should().Contain("Add rate limit");
        // No literal markdown source should leak through.
        output.Should().NotContain("# Vulnerability");
        output.Should().NotContain("```");
    }

    [Fact]
    public void Python_fence_still_renders_as_code_block_unchanged()
    {
        // Make sure the unwrap branch doesn't apply to non-markdown languages — we want
        // ```python and friends to keep their code-block panel.
        var output = Render(
            "```python\n" +
            "# This comment must survive as code, not become an h1\n" +
            "print('hi')\n" +
            "```\n");

        // The '#' on the first line must be preserved as a literal — if the unwrap branch
        // mis-fired, ConsumeCodeFence would have re-rendered it as a heading instead.
        output.Should().Contain("# This comment must survive as code, not become an h1");
        output.Should().Contain("print('hi')");
    }

    [Fact]
    public void Markdown_fence_with_attributes_after_language_still_unwraps()
    {
        // Pandoc-style `{attrs}` after the language hint shouldn't disable the unwrap path.
        // ExtractFenceLanguage stops at whitespace so `markdown {.numbered}` → "markdown".
        var output = Render(
            "```markdown {.numbered}\n" +
            "# Heading\n" +
            "```\n");

        output.Should().Contain("Heading");
        output.Should().NotContain("# Heading");
    }
}
