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
    public async Task Stream_json_pipeline_emits_two_assistant_events_then_a_result_event()
    {
        // Round 1: model emits a tool call. Round 2: model finishes with text.
        // The Anthropic schema buffers each round into one "assistant" event (containing the
        // text + tool_use blocks for that iteration) and emits one terminal "result" event.
        // Per-delta text_delta / tool_call / tool_result events from old zdt are intentionally
        // gone — claude doesn't emit them either.
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
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 },
            observer: observer);

        using var session = Session.NewEphemeral("test-model");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        events.Select(e => e.GetProperty("type").GetString()).Should().Equal(
            "assistant", "assistant", "result");

        // Iteration 1: assistant event carrying the tool_use block.
        var iter1Content = events[0].GetProperty("message").GetProperty("content");
        var toolUse = iter1Content.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "tool_use");
        toolUse.GetProperty("name").GetString().Should().Be("Echo");
        toolUse.GetProperty("id").GetString().Should().Be("c1");
        toolUse.GetProperty("input").GetProperty("text").GetString().Should().Be("hi");

        // Iteration 2: assistant event with the final text and no tool_use.
        var iter2Content = events[1].GetProperty("message").GetProperty("content");
        iter2Content[0].GetProperty("text").GetString().Should().Be("all done");

        // Terminal result event.
        var result = events[2];
        result.GetProperty("subtype").GetString().Should().Be("success");
        result.GetProperty("is_error").GetBoolean().Should().BeFalse();
        result.GetProperty("num_turns").GetInt32().Should().Be(2);
        result.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        result.GetProperty("result").GetString().Should().Be("all done");
    }

    [Fact]
    public async Task Stream_json_pipeline_emits_rate_limit_event_then_error_result_on_429()
    {
        // Upstream returns 429 with Retry-After: 60; with maxRetries=0 the LiteLLMClient
        // throws RateLimitException immediately. AgentLoop must catch it, emit a structured
        // rate_limit_event to the observer, then a terminal result event with is_error=true
        // — exactly the two events AppSec-Automator's DetectsRateLimit + StreamJsonResult
        // both look for.
        var resp = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limit hit", Encoding.UTF8, "text/plain"),
        };
        resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));

        var handler = new StubHandler(resp);
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var registry = new ToolRegistry();
        var sw = new StringWriter();
        IAgentObserver observer = new StreamJsonObserver(sw);

        var agent = new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 },
            observer: observer);

        using var session = Session.NewEphemeral("test-model");

        Func<Task> act = async () =>
            await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        await act.Should().ThrowAsync<RateLimitException>();

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        events.Select(e => e.GetProperty("type").GetString()).Should().Equal(
            "rate_limit_event", "result");

        var rl = events[0];
        var info = rl.GetProperty("rate_limit_info");
        info.GetProperty("status").GetString().Should().Be("rejected");
        info.GetProperty("resetsAt").GetInt64()
            .Should().BeInRange(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 50,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 70);

        var result = events[1];
        result.GetProperty("subtype").GetString().Should().Be("error_during_execution");
        result.GetProperty("is_error").GetBoolean().Should().BeTrue();
        result.GetProperty("stop_reason").GetString().Should().Be("rate_limited");
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
