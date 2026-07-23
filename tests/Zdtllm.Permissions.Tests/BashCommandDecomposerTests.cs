using Zdtllm.Permissions;

namespace Zdtllm.Permissions.Tests;

public sealed class BashCommandDecomposerTests
{
    [Fact]
    public void Single_command_yields_one_segment()
    {
        BashCommandDecomposer.Decompose("git diff --cached")
            .Should().ContainSingle().Which.Should().Be("git diff --cached");
    }

    [Theory]
    [InlineData("git diff && rm -rf /", new[] { "git diff", "rm -rf /" })]
    [InlineData("a; b; c", new[] { "a", "b", "c" })]
    [InlineData("cat x | grep y", new[] { "cat x", "grep y" })]
    [InlineData("build || echo fail", new[] { "build", "echo fail" })]
    [InlineData("run &", new[] { "run" })]
    [InlineData("first\nsecond", new[] { "first", "second" })]
    public void Splits_on_shell_control_operators(string cmd, string[] expected)
    {
        BashCommandDecomposer.Decompose(cmd).Should().Equal(expected);
    }

    [Fact]
    public void Operators_inside_quotes_are_not_separators()
    {
        BashCommandDecomposer.Decompose("echo 'a && b'")
            .Should().ContainSingle().Which.Should().Be("echo 'a && b'");
        BashCommandDecomposer.Decompose("echo \"x | y\"")
            .Should().ContainSingle().Which.Should().Be("echo \"x | y\"");
    }

    [Fact]
    public void Escaped_operator_is_literal_not_a_separator()
    {
        BashCommandDecomposer.Decompose(@"echo a \&\& b").Should().ContainSingle();
    }

    [Theory]
    [InlineData("git diff $(rm -rf /)", true)]
    [InlineData("echo `whoami`", true)]
    [InlineData("diff <(a) <(b)", true)]
    [InlineData("cat >(tee log)", true)]
    [InlineData("echo '$(safe literal)'", false)] // single-quoted → literal, not a substitution
    [InlineData("git diff --cached", false)]
    public void Detects_command_substitution(string cmd, bool expected)
    {
        BashCommandDecomposer.HasCommandSubstitution(cmd).Should().Be(expected);
    }
}
