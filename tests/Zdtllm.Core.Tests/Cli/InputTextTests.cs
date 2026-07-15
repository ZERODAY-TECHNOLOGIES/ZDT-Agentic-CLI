using Zdtllm.Cli.Input;

namespace Zdtllm.Core.Tests.Cli;

public sealed class InputTextTests
{
    // ESC control char built without a string escape on purpose: \x1b is a greedy escape that
    // would swallow following hex digits (e.g. "\x1bb" == U+01BB, not ESC + 'b').
    private static readonly string Esc = ((char)27).ToString();

    [Fact]
    public void Strips_bracketed_paste_markers()
    {
        var raw = $"{Esc}[200~hello\nworld{Esc}[201~";

        InputText.StripBracketedPasteMarkers(raw).Should().Be("hello\nworld");
    }

    [Fact]
    public void Strips_bare_markers_and_lone_escapes()
    {
        InputText.StripBracketedPasteMarkers("[200~data[201~").Should().Be("data");
        InputText.StripBracketedPasteMarkers($"a{Esc}b").Should().Be("ab");
    }

    [Theory]
    [InlineData("\"C:\\path\\to file.png\"", "C:\\path\\to file.png")]
    [InlineData("'/home/user/report.pdf'", "/home/user/report.pdf")]
    [InlineData("/home/user/notes.txt", "/home/user/notes.txt")]
    public void Normalizes_dropped_paths_by_stripping_quotes(string input, string expected)
    {
        InputText.NormalizeDroppedPath(input).Should().Be(expected);
    }

    [Fact]
    public void Unescapes_unix_dragged_spaces_only_when_no_real_spaces()
    {
        InputText.NormalizeDroppedPath("/home/user/my\\ file.txt").Should().Be("/home/user/my file.txt");
        // Leaves prose (which has real, unescaped spaces) alone.
        InputText.NormalizeDroppedPath("read the file\\ now").Should().Be("read the file\\ now");
    }

    [Fact]
    public void Reconstruct_burst_maps_enter_to_newline_and_keeps_printables()
    {
        var keys = new List<ConsoleKeyInfo>
        {
            new('h', ConsoleKey.H, false, false, false),
            new('i', ConsoleKey.I, false, false, false),
            new('\r', ConsoleKey.Enter, false, false, false),
            new('x', ConsoleKey.X, false, false, false),
        };

        InputText.ReconstructBurst(keys).Should().Be("hi\nx");
    }

    [Fact]
    public void Looks_like_existing_path_detects_a_real_file()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "zdt-drop-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, "x");
        try
        {
            InputText.LooksLikeExistingPath($"\"{tmp}\"", out var norm).Should().BeTrue();
            norm.Should().Be(tmp);

            InputText.LooksLikeExistingPath("definitely not a path", out _).Should().BeFalse();
        }
        finally { File.Delete(tmp); }
    }
}
