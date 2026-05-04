using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Observers;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Observers;

/// <summary>
/// End-to-end check that the AgentLoop fires the right observer events through a real run
/// — including a tool round trip — using a stub LiteLLM. Catches missing call sites and
/// ordering bugs that pure-observer unit tests don't.
/// </summary>
public sealed class AgentLoopObserverIntegrationTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    [Fact]
    public async Task Stream_json_pipeline_records_text_then_tool_then_final_in_order()
    {
        // Round 1: model emits a tool call. Round 2: model finishes with text.
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"hi\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"all done\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(Sse(round1), Sse(round2));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var registry = new ToolRegistry();
        registry.Register(new EchoTool());

        var sw = new StringWriter();
        IAgentObserver observer = new StreamJsonObserver(sw);

        var agent = new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test", MaxTurns = 5 },
            observer: observer);

        using var session = Session.NewEphemeral("test");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        events.Select(e => e.GetProperty("type").GetString()).Should().ContainInOrder(
            "tool_call", "tool_result", "text_delta", "final");

        var toolCall = events.First(e => e.GetProperty("type").GetString() == "tool_call");
        toolCall.GetProperty("name").GetString().Should().Be("Echo");
        toolCall.GetProperty("arguments").GetProperty("text").GetString().Should().Be("hi");

        var toolResult = events.First(e => e.GetProperty("type").GetString() == "tool_result");
        toolResult.GetProperty("content").GetString().Should().Be("echoed:hi");
        toolResult.GetProperty("is_error").GetBoolean().Should().BeFalse();

        var final = events.First(e => e.GetProperty("type").GetString() == "final");
        final.GetProperty("text").GetString().Should().Be("all done");
        final.GetProperty("turns").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Tools_allowlist_drops_non_listed_tools_from_registry()
    {
        var registry = new ToolRegistry();
        registry.Register(new EchoTool());
        registry.Register(new ReadTool());
        registry.Register(new WriteTool());

        // Apply allowlist via the public Remove API the CLI helper uses.
        var keep = new HashSet<string>(new[] { "Read", "Echo" }, StringComparer.Ordinal);
        foreach (var name in registry.All.Select(t => t.Schema.Name).Where(n => !keep.Contains(n)).ToList())
            registry.Remove(name);

        registry.All.Select(t => t.Schema.Name).OrderBy(n => n)
            .Should().Equal("Echo", "Read");
    }

    private sealed class EchoTool : ITool
    {
        public ToolSchema Schema { get; } = new(
            "Echo",
            "Echo back the text argument prefixed with 'echoed:'.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { text = new { type = "string" } },
                required = new[] { "text" },
            }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var text = args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            return Task.FromResult(ToolResult.Success($"echoed:{text}"));
        }
    }
}
