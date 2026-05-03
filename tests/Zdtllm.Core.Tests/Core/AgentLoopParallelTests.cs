using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

public sealed class AgentLoopParallelTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    /// <summary>
    /// Round 1: emit two native tool_calls with the same tool name, two distinct ids.
    /// Round 2: emit a plain assistant text and stop.
    /// </summary>
    private static (string round1, string round2) BuildTwoToolCallRounds(string toolName)
    {
        var round1 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0, id = "c1", type = "function",
                                    function = new { name = toolName, arguments = "{\"i\":1}" },
                                },
                                new
                                {
                                    index = 1, id = "c2", type = "function",
                                    function = new { name = toolName, arguments = "{\"i\":2}" },
                                },
                            },
                        },
                    },
                },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "tool_calls" } },
            }) + "\n\n" +
            "data: [DONE]\n\n";

        var round2 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = "ok" } } },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop" } },
            }) + "\n\n" +
            "data: [DONE]\n\n";

        return (round1, round2);
    }

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test", MaxTurns = 5 });
    }

    [Fact]
    public async Task Two_parallel_safe_calls_in_one_turn_overlap_in_time()
    {
        var tool = new SleepyTool("Sleeper", canRunInParallel: true, sleepMs: 100);
        var registry = new ToolRegistry();
        registry.Register(tool);

        var (r1, r2) = BuildTwoToolCallRounds("Sleeper");
        var handler = new StubHandler(Sse(r1), Sse(r2));
        var agent = BuildAgent(handler, registry);

        using var session = Session.NewEphemeral("test");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        tool.Records.Should().HaveCount(2);
        var (start1, end1) = (tool.Records[0].Start, tool.Records[0].End);
        var (start2, end2) = (tool.Records[1].Start, tool.Records[1].End);

        // Parallel: the later start fires before the earlier end.
        var laterStart = start1 > start2 ? start1 : start2;
        var earlierEnd = end1 < end2 ? end1 : end2;
        laterStart.Should().BeBefore(earlierEnd, "calls were dispatched concurrently");
    }

    [Fact]
    public async Task Two_calls_with_an_unsafe_tool_run_strictly_serially()
    {
        var tool = new SleepyTool("Lock", canRunInParallel: false, sleepMs: 100);
        var registry = new ToolRegistry();
        registry.Register(tool);

        var (r1, r2) = BuildTwoToolCallRounds("Lock");
        var handler = new StubHandler(Sse(r1), Sse(r2));
        var agent = BuildAgent(handler, registry);

        using var session = Session.NewEphemeral("test");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        tool.Records.Should().HaveCount(2);
        var (start1, end1) = (tool.Records[0].Start, tool.Records[0].End);
        var (start2, end2) = (tool.Records[1].Start, tool.Records[1].End);

        // Sequential: the second start is at or after the first end.
        var firstEnd = end1 < end2 ? end1 : end2;
        var secondStart = start1 > start2 ? start1 : start2;
        secondStart.Should().BeOnOrAfter(firstEnd, "calls were dispatched serially");
    }

    [Fact]
    public async Task Single_call_in_a_turn_takes_the_serial_path_unchanged()
    {
        // Simple regression check: with one tool call, behaviour must be identical to
        // before the parallel dispatch refactor. We assert by side-effect: tool is called
        // exactly once and the agent's final text matches the second-round response.
        var tool = new SleepyTool("Once", canRunInParallel: true, sleepMs: 5);
        var registry = new ToolRegistry();
        registry.Register(tool);

        var round1 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0, id = "c1", type = "function",
                                    function = new { name = "Once", arguments = "{}" },
                                },
                            },
                        },
                    },
                },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls" } } }) + "\n\n" +
            "data: [DONE]\n\n";

        var round2 =
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "done" } } } }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop" } } }) + "\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(Sse(round1), Sse(round2));
        var agent = BuildAgent(handler, registry);

        using var session = Session.NewEphemeral("test");
        var result = await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        tool.Records.Should().ContainSingle();
        result.FinalText.Should().Be("done");
    }

    private sealed class SleepyTool : ITool
    {
        private readonly int _sleepMs;
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset Start, DateTimeOffset End)> _records = new();

        public SleepyTool(string name, bool canRunInParallel, int sleepMs)
        {
            Schema = new ToolSchema(
                name,
                "test sleeper",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));
            CanRunInParallel = canRunInParallel;
            _sleepMs = sleepMs;
        }

        public ToolSchema Schema { get; }
        public bool CanRunInParallel { get; }
        public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Records
        {
            get { lock (_gate) return _records.ToList(); }
        }

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var start = DateTimeOffset.UtcNow;
            await Task.Delay(_sleepMs, ct).ConfigureAwait(false);
            var end = DateTimeOffset.UtcNow;
            lock (_gate) _records.Add((start, end));
            return ToolResult.Success("ok");
        }
    }
}
