using Zdtllm.Permissions;

namespace Zdtllm.Permissions.Tests;

public sealed class PermissionRuleSetTests
{
    private static PermissionRuleSet Build(
        IReadOnlyList<string>? allow = null,
        IReadOnlyList<string>? ask = null,
        IReadOnlyList<string>? deny = null) =>
        PermissionRuleSet.Build(allow ?? [], ask ?? [], deny ?? []);

    [Fact]
    public void No_rules_returns_Ask_for_permission_required_tool()
    {
        var rs = PermissionRuleSet.Empty;
        rs.Evaluate("Bash", "ls -la").Should().Be(PermissionDecision.Ask);
        rs.Evaluate("Edit", "/etc/hosts").Should().Be(PermissionDecision.Ask);
        rs.Evaluate("Write", "out.txt").Should().Be(PermissionDecision.Ask);
        rs.Evaluate("WebFetch", "https://x").Should().Be(PermissionDecision.Ask);
        rs.Evaluate("WebSearch", "query").Should().Be(PermissionDecision.Ask);
        rs.Evaluate("Skill", "my-skill").Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public void No_rules_returns_Allow_for_non_required_tool()
    {
        var rs = PermissionRuleSet.Empty;
        rs.Evaluate("Read", "/path").Should().Be(PermissionDecision.Allow);
        rs.Evaluate("Glob", "**/*.cs").Should().Be(PermissionDecision.Allow);
        rs.Evaluate("Grep", "TODO").Should().Be(PermissionDecision.Allow);
        rs.Evaluate("TodoWrite", null).Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Allow_rule_with_wildcard_matches()
    {
        var rs = Build(allow: ["Bash(git diff *)"]);
        rs.Evaluate("Bash", "git diff --cached").Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Bare_tool_allow_rule_matches_any_specifier()
    {
        var rs = Build(allow: ["Bash"]);
        rs.Evaluate("Bash", "rm -rf /").Should().Be(PermissionDecision.Allow);
        rs.Evaluate("Bash", "echo hi").Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Bare_tool_rule_matches_when_no_specifier_provided()
    {
        var rs = Build(allow: ["TodoWrite"]);
        rs.Evaluate("TodoWrite", null).Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Specific_rule_does_not_match_when_no_specifier_provided()
    {
        var rs = Build(allow: ["Bash(git diff *)"]);
        // No specifier → specific-rule branch skipped → fallthrough to default for required tool
        rs.Evaluate("Bash", null).Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public void Different_tool_name_does_not_match()
    {
        var rs = Build(allow: ["Read"]);
        rs.Evaluate("Bash", "ls").Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public void Specific_specifier_does_not_match_unrelated_input()
    {
        var rs = Build(allow: ["Bash(git diff *)"]);
        rs.Evaluate("Bash", "git push origin").Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public void Deny_takes_precedence_over_allow_when_both_match()
    {
        var rs = Build(
            allow: ["Read"],
            deny: ["Read(./.env)"]);

        rs.Evaluate("Read", "./.env").Should().Be(PermissionDecision.Deny);
        rs.Evaluate("Read", "./README.md").Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Deny_takes_precedence_over_ask()
    {
        var rs = Build(
            ask: ["Bash(git push *)"],
            deny: ["Bash(git push --force *)"]);

        rs.Evaluate("Bash", "git push --force origin main").Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public void Ask_takes_precedence_over_allow()
    {
        var rs = Build(
            allow: ["Bash"],
            ask: ["Bash(git push *)"]);

        rs.Evaluate("Bash", "git push origin").Should().Be(PermissionDecision.Ask);
        rs.Evaluate("Bash", "ls").Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Recursive_wildcard_in_path_specifier_matches_subdirectories()
    {
        var rs = Build(deny: ["Read(./secrets/**)"]);

        rs.Evaluate("Read", "./secrets/aws/key.pem").Should().Be(PermissionDecision.Deny);
        rs.Evaluate("Read", "./secrets/db/creds.json").Should().Be(PermissionDecision.Deny);
        rs.Evaluate("Read", "./public/readme.md").Should().Be(PermissionDecision.Allow);
    }

    [Fact]
    public void Multiple_deny_patterns_short_circuit_on_first_match()
    {
        var rs = Build(deny: ["Read(./.env)", "Read(./.env.*)", "Read(./secrets/**)"]);

        rs.Evaluate("Read", "./.env").Should().Be(PermissionDecision.Deny);
        rs.Evaluate("Read", "./.env.production").Should().Be(PermissionDecision.Deny);
        rs.Evaluate("Read", "./secrets/x").Should().Be(PermissionDecision.Deny);
    }

    [Fact]
    public void RequiresPermission_recognises_listed_tools()
    {
        PermissionRuleSet.RequiresPermission("Bash").Should().BeTrue();
        PermissionRuleSet.RequiresPermission("Edit").Should().BeTrue();
        PermissionRuleSet.RequiresPermission("Write").Should().BeTrue();
        PermissionRuleSet.RequiresPermission("WebFetch").Should().BeTrue();
        PermissionRuleSet.RequiresPermission("WebSearch").Should().BeTrue();
        PermissionRuleSet.RequiresPermission("Skill").Should().BeTrue();

        PermissionRuleSet.RequiresPermission("Read").Should().BeFalse();
        PermissionRuleSet.RequiresPermission("Glob").Should().BeFalse();
        PermissionRuleSet.RequiresPermission("Grep").Should().BeFalse();
        PermissionRuleSet.RequiresPermission("TodoWrite").Should().BeFalse();
    }

    [Fact]
    public void Tool_name_matching_is_case_sensitive()
    {
        // Rule uses lowercase 'bash' which is NOT the canonical tool name. A real
        // call to 'Bash' (canonical, permission-required) must NOT match the rule
        // and must therefore fall through to the default Ask decision.
        var rs = Build(allow: ["bash"]);
        rs.Evaluate("Bash", "ls").Should().Be(PermissionDecision.Ask);
    }

    [Fact]
    public void Build_throws_on_malformed_rule()
    {
        var act = () => PermissionRuleSet.Build(allow: ["Bash(unclosed"], ask: [], deny: []);
        act.Should().Throw<PermissionRuleParseException>();
    }
}
