using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Agents;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// SubagentRunner surfacing and dispatching project (team-mode) subagents alongside the built-ins.
/// </summary>
public sealed class SubagentRunnerProjectAgentTests
{
    private static AgentLoop BuildParentAgent(ToolRegistry registry)
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(client, registry, PermissionRuleSet.Empty, new AgentLoopOptions { Model = "m" });
    }

    private sealed class StubTool : ITool
    {
        private readonly string _name;
        public StubTool(string name) => _name = name;
        public ToolSchema Schema => new(_name, $"{_name}.",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));
        public string? GetSpecifierForPermissions(JsonElement args) => null;
        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    [Fact]
    public void Registry_from_definition_honours_allowed_tools_and_never_includes_agent()
    {
        var parent = new ToolRegistry();
        parent.Register(new StubTool("Read"));
        parent.Register(new StubTool("Write"));
        parent.Register(new StubTool(TaskTool.ToolName)); // "Agent"

        var def = new AgentDefinition("x", "d",
            new HashSet<string>(StringComparer.Ordinal) { "Read", TaskTool.ToolName }, "p", null);

        var sub = SubagentRunner.BuildRegistryForDefinition(def, parent);

        // Read is allowed; Agent is excluded even though the definition listed it; Write wasn't allowed.
        sub.All.Select(t => t.Schema.Name).Should().Equal("Read");
    }

    [Fact]
    public void Null_allowed_tools_gives_the_general_purpose_profile()
    {
        var parent = new ToolRegistry();
        parent.Register(new StubTool("Read"));
        parent.Register(new StubTool("Write"));
        parent.Register(new StubTool(TaskTool.ToolName));

        var def = new AgentDefinition("x", "d", AllowedTools: null, "p", null);
        var sub = SubagentRunner.BuildRegistryForDefinition(def, parent);

        // general-purpose = everything except the Agent tool.
        sub.All.Select(t => t.Schema.Name).OrderBy(n => n).Should().Equal("Read", "Write");
    }

    [Fact]
    public void Project_agents_are_supported_and_listed_alongside_builtins()
    {
        var reg = new TeamAgentRegistry(new[]
        {
            new AgentDefinition("db-migrator", "runs migrations",
                new HashSet<string>(StringComparer.Ordinal) { "Read", "Bash" }, "p", null),
        });
        var runner = new SubagentRunner(BuildParentAgent(new ToolRegistry()), teamAgents: reg);

        runner.SupportsType("db-migrator").Should().BeTrue();
        runner.SupportsType("general-purpose").Should().BeTrue();
        runner.SupportsType("nope").Should().BeFalse();

        runner.AvailableTypes.Should().Contain("db-migrator");
        runner.AvailableTypes.Should().Contain("general-purpose");

        var info = runner.GetTypeInfo().Single(i => i.Name == "db-migrator");
        info.Description.Should().Be("runs migrations");
        info.AllowedTools.Should().Equal("Bash", "Read");
    }

    [Fact]
    public void Without_a_registry_only_the_builtins_are_available()
    {
        var runner = new SubagentRunner(BuildParentAgent(new ToolRegistry()));

        runner.AvailableTypes.Should().Equal("general-purpose", "code-reviewer", "explore");
        runner.SupportsType("db-migrator").Should().BeFalse();
    }
}
