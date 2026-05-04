using Zdtllm.Mcp;

namespace Zdtllm.Core.Tests.Mcp;

public sealed class McpConfigParserTests
{
    [Fact]
    public void Parses_a_minimal_single_server_block()
    {
        var json = """
        {
          "mcpServers": {
            "fs": { "command": "node", "args": ["server.js"] }
          }
        }
        """;
        var servers = McpConfigParser.Parse(json);

        servers.Should().ContainSingle();
        servers[0].Name.Should().Be("fs");
        servers[0].Command.Should().Be("node");
        servers[0].Args.Should().Equal("server.js");
        servers[0].Env.Should().BeEmpty();
    }

    [Fact]
    public void Parses_env_dictionary_and_multiple_servers()
    {
        var json = """
        {
          "mcpServers": {
            "a": { "command": "x" },
            "b": {
              "command": "y",
              "args": ["--flag", "value"],
              "env": { "TOKEN": "abc", "PORT": 8080, "ENABLED": true }
            }
          }
        }
        """;
        var servers = McpConfigParser.Parse(json);

        servers.Should().HaveCount(2);
        var b = servers.Single(s => s.Name == "b");
        b.Args.Should().Equal("--flag", "value");
        b.Env.Should().ContainKey("TOKEN").WhoseValue.Should().Be("abc");
        b.Env.Should().ContainKey("PORT").WhoseValue.Should().Be("8080");
        b.Env.Should().ContainKey("ENABLED").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void Empty_or_missing_mcpServers_yields_empty_list_not_error()
    {
        McpConfigParser.Parse("""{ }""").Should().BeEmpty();
        McpConfigParser.Parse("""{ "mcpServers": {} }""").Should().BeEmpty();
        McpConfigParser.Parse("""{ "mcpServers": null }""").Should().BeEmpty();
    }

    [Fact]
    public void Throws_with_source_label_when_command_is_missing()
    {
        var json = """{ "mcpServers": { "broken": { "args": ["x"] } } }""";
        var act = () => McpConfigParser.Parse(json, sourceLabel: "/tmp/cfg.json");

        act.Should().Throw<McpConfigException>()
            .WithMessage("/tmp/cfg.json: server 'broken' is missing required 'command' string.");
    }

    [Fact]
    public void Throws_when_top_level_is_not_an_object()
    {
        var act = () => McpConfigParser.Parse("[]");
        act.Should().Throw<McpConfigException>().WithMessage("*top-level must be an object*");
    }

    [Fact]
    public void Throws_on_malformed_json()
    {
        var act = () => McpConfigParser.Parse("{ not json", sourceLabel: "x.json");
        act.Should().Throw<McpConfigException>().WithMessage("x.json: not valid JSON*");
    }

    [Fact]
    public void Merge_lets_later_sources_override_earlier_ones_per_server()
    {
        var first = McpConfigParser.Parse("""{"mcpServers":{"a":{"command":"x1"},"b":{"command":"y"}}}""");
        var second = McpConfigParser.Parse("""{"mcpServers":{"a":{"command":"x2","args":["new"]}}}""");

        var merged = McpConfigParser.Merge(first, second);

        merged.Should().HaveCount(2);
        var a = merged.Single(s => s.Name == "a");
        a.Command.Should().Be("x2");
        a.Args.Should().Equal("new");
        merged.Single(s => s.Name == "b").Command.Should().Be("y");
    }

    [Fact]
    public void ParseFile_reports_a_clean_message_when_file_is_missing()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".json");
        var act = () => McpConfigParser.ParseFile(bogus);
        act.Should().Throw<McpConfigException>().WithMessage("--mcp-config: file not found:*");
    }

    [Fact]
    public void ParseFile_reads_disk_and_parses_the_contents()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcp-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "mcpServers": { "x": { "command": "ls" } } }""");
            var servers = McpConfigParser.ParseFile(path);
            servers.Should().ContainSingle();
            servers[0].Command.Should().Be("ls");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
