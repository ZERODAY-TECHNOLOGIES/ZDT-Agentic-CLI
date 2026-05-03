using Zdtllm.Permissions;

namespace Zdtllm.Permissions.Tests;

public sealed class RuleParserTests
{
    [Fact]
    public void Parses_bare_tool_name()
    {
        var rule = RuleParser.Parse("Read");
        rule.ToolName.Should().Be("Read");
        rule.Specifier.Should().BeNull();
        rule.Pattern.Should().BeNull();
    }

    [Fact]
    public void Parses_tool_with_specifier()
    {
        var rule = RuleParser.Parse("Bash(git diff *)");
        rule.ToolName.Should().Be("Bash");
        rule.Specifier.Should().Be("git diff *");
        rule.Pattern.Should().NotBeNull();
    }

    [Fact]
    public void Trims_whitespace_around_rule()
    {
        var rule = RuleParser.Parse("  Read(./.env)  ");
        rule.ToolName.Should().Be("Read");
        rule.Specifier.Should().Be("./.env");
    }

    [Fact]
    public void Empty_specifier_is_valid_and_matches_empty_string()
    {
        var rule = RuleParser.Parse("Tool()");
        rule.ToolName.Should().Be("Tool");
        rule.Specifier.Should().Be("");
        rule.Pattern!.IsMatch("").Should().BeTrue();
        rule.Pattern.IsMatch("x").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_on_empty_rule(string raw)
    {
        var act = () => RuleParser.Parse(raw);
        act.Should().Throw<PermissionRuleParseException>();
    }

    [Fact]
    public void Throws_on_missing_close_paren()
    {
        var act = () => RuleParser.Parse("Bash(git diff *");
        act.Should().Throw<PermissionRuleParseException>()
            .WithMessage("*mismatched parentheses*");
    }

    [Fact]
    public void Throws_on_invalid_tool_name()
    {
        var act = () => RuleParser.Parse("123Bash(x)");
        act.Should().Throw<PermissionRuleParseException>()
            .WithMessage("*invalid tool name*");
    }

    [Fact]
    public void Throws_on_only_specifier_no_tool()
    {
        var act = () => RuleParser.Parse("(noop)");
        act.Should().Throw<PermissionRuleParseException>()
            .WithMessage("*missing tool name*");
    }
}
