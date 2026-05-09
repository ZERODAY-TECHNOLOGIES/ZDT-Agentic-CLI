using Zdtllm.Cli;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// Covers the argv → ParsedArgs translation. Focus areas:
///   • Anthropic-compat for --tools / --allowed-tools (space-separated positional list).
///   • Backward-compat for the legacy comma-separated form.
///   • Boundary detection: a multi-value flag must stop on the next "--flag".
///   • Boolean flags, values, multi-value collectors (--add-dir, --mcp-config), positional query.
/// </summary>
public sealed class ArgumentParserTests
{
    [Fact]
    public void Tools_accepts_space_separated_positional_list()
    {
        // The exact form claude-cli / AppSec-Automator emits.
        var parsed = ArgumentParser.Parse(["--tools", "Read", "Glob", "Grep"]);

        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep");
    }

    [Fact]
    public void AllowedTools_is_alias_for_tools()
    {
        var parsed = ArgumentParser.Parse(["--allowed-tools", "Read", "Glob", "Grep", "Agent"]);

        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep", "Agent");
    }

    [Fact]
    public void Tools_keeps_backward_compat_with_comma_separated()
    {
        // Legacy zdt form, still supported.
        var parsed = ArgumentParser.Parse(["--tools", "Read,Glob,Grep"]);

        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep");
    }

    [Fact]
    public void Tools_accepts_mixed_space_and_comma_separated()
    {
        var parsed = ArgumentParser.Parse(["--tools", "Read,Glob", "Grep", "Agent"]);

        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep", "Agent");
    }

    [Fact]
    public void Tools_accepts_mcp_namespaced_tool_names()
    {
        // DAST/Network agent form: mcp__server__tool entries mixed with FS tools.
        var parsed = ArgumentParser.Parse([
            "--allowed-tools",
            "mcp__dast__ssh_exec", "mcp__dast__record_finding", "Read", "Glob", "Grep",
        ]);

        parsed.AllowedTools.Should().Equal(
            "mcp__dast__ssh_exec", "mcp__dast__record_finding", "Read", "Glob", "Grep");
    }

    [Fact]
    public void Tools_stops_at_next_flag()
    {
        // The list ends at --max-turns; "10" is consumed by --max-turns, not by --tools.
        var parsed = ArgumentParser.Parse([
            "--tools", "Read", "Glob", "Grep", "Agent",
            "--max-turns", "10",
            "--print",
        ]);

        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep", "Agent");
        parsed.MaxTurns.Should().Be(10);
        parsed.PrintMode.Should().BeTrue();
    }

    [Fact]
    public void Tools_stops_at_short_flag()
    {
        // Anything matching "-X" where X is a letter is also a flag boundary.
        var parsed = ArgumentParser.Parse([
            "--tools", "Read", "Glob",
            "-p",
            "do the thing",
        ]);

        parsed.AllowedTools.Should().Equal("Read", "Glob");
        parsed.PrintMode.Should().BeTrue();
        parsed.Query.Should().Be("do the thing");
    }

    [Fact]
    public void Tools_with_no_values_throws()
    {
        // Bare --tools at the end of argv, or followed immediately by another flag.
        var act = () => ArgumentParser.Parse(["--tools"]);
        act.Should().Throw<ArgumentException>().WithMessage("*requires at least one value*");

        var act2 = () => ArgumentParser.Parse(["--tools", "--print"]);
        act2.Should().Throw<ArgumentException>().WithMessage("*requires at least one value*");
    }

    [Fact]
    public void Print_and_positional_query_combine_into_query_string()
    {
        var parsed = ArgumentParser.Parse(["-p", "scan", "the", "repo"]);

        parsed.PrintMode.Should().BeTrue();
        parsed.Query.Should().Be("scan the repo");
    }

    [Fact]
    public void Print_with_no_query_leaves_query_null_for_caller_to_handle_stdin()
    {
        // The stdin-fallback decision is made in Program.RunAsync, not in the parser:
        // the parser just leaves Query null when nothing positional was supplied.
        var parsed = ArgumentParser.Parse(["-p"]);

        parsed.PrintMode.Should().BeTrue();
        parsed.Query.Should().BeNull();
    }

    [Fact]
    public void McpConfig_is_repeatable()
    {
        var parsed = ArgumentParser.Parse([
            "--mcp-config", "a.json",
            "--mcp-config", "b.json",
        ]);

        parsed.McpConfigs.Should().Equal("a.json", "b.json");
    }

    [Fact]
    public void Common_claude_invocation_parses_end_to_end()
    {
        // The exact command AppSec-Automator's ClaudeCodeService::scanRepository emits.
        var parsed = ArgumentParser.Parse([
            "--print", "--verbose", "--output-format", "stream-json",
            "--tools", "Read", "Glob", "Grep",
            "--model", "claude-opus-4-7",
        ]);

        parsed.PrintMode.Should().BeTrue();
        parsed.Verbose.Should().BeTrue();
        parsed.OutputFormat.Should().Be("stream-json");
        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep");
        parsed.Model.Should().Be("claude-opus-4-7");
        parsed.Query.Should().BeNull(); // prompt arrives via stdin
    }

    [Fact]
    public void Research_invocation_with_max_turns_and_agent_tool()
    {
        // ResearchService::chat form: --tools Read Glob Grep Agent --max-turns 50.
        var parsed = ArgumentParser.Parse([
            "--print", "--verbose", "--output-format", "stream-json",
            "--tools", "Read", "Glob", "Grep", "Agent",
            "--max-turns", "50",
        ]);

        parsed.AllowedTools.Should().Equal("Read", "Glob", "Grep", "Agent");
        parsed.MaxTurns.Should().Be(50);
    }

    [Fact]
    public void Mcp_init_timeout_seconds_is_parsed_as_int()
    {
        var parsed = ArgumentParser.Parse([
            "--mcp-config", "dast.json",
            "--mcp-init-timeout-seconds", "60",
        ]);

        parsed.McpInitTimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public void Mcp_init_timeout_seconds_defaults_to_null_when_unset()
    {
        var parsed = ArgumentParser.Parse(["--mcp-config", "dast.json"]);

        parsed.McpInitTimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public void Require_mcp_is_a_boolean_flag()
    {
        var parsed = ArgumentParser.Parse(["--mcp-config", "dast.json", "--require-mcp"]);

        parsed.RequireMcp.Should().BeTrue();
    }

    [Fact]
    public void Require_mcp_defaults_to_false_so_legacy_invocations_keep_warning_only_behaviour()
    {
        // Historical behaviour: a misbehaving MCP server reports a warning and the run
        // continues. --require-mcp is opt-in, so existing scripts must NOT start exiting
        // non-zero on the upgrade.
        var parsed = ArgumentParser.Parse(["--mcp-config", "dast.json"]);

        parsed.RequireMcp.Should().BeFalse();
    }

    [Fact]
    public void Dast_invocation_with_mcp_config_and_allowed_tools()
    {
        // DastAgentService::run form.
        var parsed = ArgumentParser.Parse([
            "--print", "--verbose", "--output-format", "stream-json",
            "--mcp-config", "/tmp/dast-mcp.json",
            "--allowed-tools",
                "mcp__dast__ssh_exec", "mcp__dast__record_finding", "mcp__dast__end_engagement",
                "Read", "Glob", "Grep",
        ]);

        parsed.McpConfigs.Should().Equal("/tmp/dast-mcp.json");
        parsed.AllowedTools.Should().Equal(
            "mcp__dast__ssh_exec", "mcp__dast__record_finding", "mcp__dast__end_engagement",
            "Read", "Glob", "Grep");
    }
}
