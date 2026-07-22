using Xunit;
using Zdtllm.Cli;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// The fleet view renders streamed subagent lines inside a Spectre Panel; raw ANSI escapes and
/// tabs would be measured as printable width and tear the panel borders (lines overlapping each
/// other). These lock in the sanitizer that keeps the panel geometry correct.
/// NB: control chars are built with (char) casts — C#'s variable-length hex string escape
/// swallows trailing hex digits (the classic "backslash-x07b is U+007B" pitfall).
/// </summary>
public class AgentFleetViewSanitizeTests
{
    private static readonly string Esc = ((char)0x1B).ToString();
    private static readonly string Bel = ((char)0x07).ToString();

    [Fact]
    public void StripsSgrColorSequences()
    {
        var line = Esc + "[38;2;239;68;68m[Read]" + Esc + "[0m {\"file_path\": \"a.php\"}";
        Assert.Equal("[Read] {\"file_path\": \"a.php\"}", AgentFleetView.SanitizeForPanel(line));
    }

    [Fact]
    public void StripsOscTitleSequences_BelAndStTerminated()
    {
        Assert.Equal("ab", AgentFleetView.SanitizeForPanel("a" + Esc + "]0;title" + Bel + "b"));
        Assert.Equal("ab", AgentFleetView.SanitizeForPanel("a" + Esc + "]0;title" + Esc + "\\b"));
    }

    [Fact]
    public void ExpandsTabsAndDropsControlChars()
    {
        Assert.Equal("a  b", AgentFleetView.SanitizeForPanel("a\tb"));
        Assert.Equal("ab", AgentFleetView.SanitizeForPanel("a" + (char)0x01 + (char)0x08 + "b"));
    }

    [Fact]
    public void PlainTextPassesThroughUnchanged()
    {
        const string line = "Let me check ShellArg::q() / ShellArg::ps().";
        Assert.Same(line, AgentFleetView.SanitizeForPanel(line));
    }

    [Fact]
    public void CursorMovementSequencesAreStripped()
    {
        Assert.Equal("xy", AgentFleetView.SanitizeForPanel("x" + Esc + "[2K" + Esc + "[1;5Hy"));
    }

    [Fact]
    public void EmptyAndNullSafe()
    {
        Assert.Equal(string.Empty, AgentFleetView.SanitizeForPanel(""));
    }
}
