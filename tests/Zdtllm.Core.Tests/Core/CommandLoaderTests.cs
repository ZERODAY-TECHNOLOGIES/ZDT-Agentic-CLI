using Zdtllm.Core.Commands;

namespace Zdtllm.Core.Tests.Core;

public sealed class CommandLoaderTests : IDisposable
{
    private readonly string _cwd;
    private readonly string _userRoot;

    public CommandLoaderTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "zdt-cmds-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(baseDir, "project");
        _userRoot = Path.Combine(baseDir, "user", "commands");
        Directory.CreateDirectory(Path.Combine(_cwd, ".zdtllm", "commands"));
        Directory.CreateDirectory(_userRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_userRoot)!, recursive: true); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_cwd)!)!, recursive: true); } catch { }
    }

    private void ProjectCmd(string name, string content) =>
        File.WriteAllText(Path.Combine(_cwd, ".zdtllm", "commands", name + ".md"), content);
    private void UserCmd(string name, string content) =>
        File.WriteAllText(Path.Combine(_userRoot, name + ".md"), content);

    private IReadOnlyList<CustomCommand> Discover() =>
        new CommandLoader().Discover(_cwd, _userRoot);

    [Fact]
    public void Expand_substitutes_arguments_and_positionals()
    {
        var cmd = new CustomCommand("x", "d", null, "Review $1 for $2 issues. Full: $ARGUMENTS");
        cmd.Expand("auth.cs security").Should().Be("Review auth.cs for security issues. Full: auth.cs security");
    }

    [Fact]
    public void Expand_missing_positionals_become_empty()
    {
        var cmd = new CustomCommand("x", "d", null, "[$1][$2][$3]");
        cmd.Expand("only").Should().Be("[only][][]");
    }

    [Fact]
    public void Discovers_a_command_with_frontmatter()
    {
        ProjectCmd("review", "---\ndescription: Review the diff\nargument-hint: <path>\n---\nReview $ARGUMENTS carefully.");

        var cmds = Discover();

        cmds.Should().ContainSingle();
        cmds[0].Name.Should().Be("review");
        cmds[0].Description.Should().Be("Review the diff");
        cmds[0].ArgumentHint.Should().Be("<path>");
        cmds[0].Body.Should().Be("Review $ARGUMENTS carefully.");
    }

    [Fact]
    public void Command_without_frontmatter_uses_the_whole_body()
    {
        ProjectCmd("hello", "Say hello to $1.");

        var cmds = Discover();

        cmds.Should().ContainSingle();
        cmds[0].Body.Should().Be("Say hello to $1.");
        cmds[0].Description.Should().Contain("/hello");
    }

    [Fact]
    public void Project_command_overrides_user_command_of_the_same_name()
    {
        UserCmd("dup", "USER version");
        ProjectCmd("dup", "PROJECT version");

        var cmds = Discover();

        cmds.Should().ContainSingle();
        cmds[0].Body.Should().Be("PROJECT version");
    }

    [Fact]
    public void Skips_names_that_collide_with_builtins()
    {
        ProjectCmd("help", "hijacked help");
        ProjectCmd("model", "hijacked model");
        ProjectCmd("safe", "ok");

        var cmds = Discover();

        cmds.Select(c => c.Name).Should().BeEquivalentTo(new[] { "safe" });
    }

    [Fact]
    public void Skips_invalid_names_and_empty_bodies()
    {
        ProjectCmd("Bad_Name", "body");  // uppercase + underscore → invalid
        ProjectCmd("empty", "   ");       // whitespace-only body

        Discover().Should().BeEmpty();
    }
}
