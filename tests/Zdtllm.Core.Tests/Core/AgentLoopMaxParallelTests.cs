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

/// <summary>
/// AgentLoopOptions.MaxParallel caps the number of in-flight tool executions when a batch
/// of parallel-safe calls fans out. Goal of the cap: protect the LiteLLM proxy / API key
/// from rate limits when the model spawns 4+ Task subagents in one turn.
/// </summary>
public sealed class AgentLoopMaxParallelTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    /// <summary>Builds a turn with N parallel calls of <paramref name="toolName"/> followed by a stop.</summary>
    private static (string round1, string round2) BuildNCallsRounds(string toolName, int n)
    {
        var calls = Enumerable.Range(0, n).Select(i => new
        {
            index = i,
            id = $"c{i}",
            type = "function",
            function = new { name = toolName, arguments = $"{{\"i\":{i}}}" },
        }).ToArray();

        var round1 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { tool_calls = calls } } },
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

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry, int maxParallel)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test", MaxTurns = 5, MaxParallel = maxParallel });
    }

    [Fact]
    public async Task Max_parallel_caps_concurrent_executions_to_the_configured_limit()
    {
        // Spin up 6 calls of a tool that sleeps 100ms; cap = 2.
        // The semaphore in DispatchToolCallsAsync should prevent more than 2 from running
        // at once — we observe this via a peak-concurrency counter inside the tool.
        var tool = new TrackingSleepyTool(sleepMs: 100);
        var registry = new ToolRegistry();
        registry.Register(tool);

        var (r1, r2) = BuildNCallsRounds("Track", 6);
        var handler = new StubHandler(Sse(r1), Sse(r2));
        var agent = BuildAgent(handler, registry, maxParallel: 2);

        using var session = Session.NewEphemeral("test");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        tool.Invocations.Should().Be(6);
        tool.PeakConcurrent.Should().BeLessThanOrEqualTo(2,
            "the semaphore should have prevented a 3rd concurrent execution");
        // We still expect SOME concurrency — otherwise the throttle is degenerating to serial.
        tool.PeakConcurrent.Should().BeGreaterThanOrEqualTo(2,
            "with 6 calls and a 2-slot semaphore, we should hit the cap at least once");
    }

    [Fact]
    public async Task Max_parallel_zero_means_unlimited_and_lets_all_calls_run_at_once()
    {
        var tool = new TrackingSleepyTool(sleepMs: 50);
        var registry = new ToolRegistry();
        registry.Register(tool);

        var (r1, r2) = BuildNCallsRounds("Track", 4);
        var handler = new StubHandler(Sse(r1), Sse(r2));
        var agent = BuildAgent(handler, registry, maxParallel: 0);

        using var session = Session.NewEphemeral("test");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        tool.Invocations.Should().Be(4);
        // With no cap, all 4 should overlap (or at least 3 — scheduler latency could squash
        // the first one's window before the last starts on slow CI).
        tool.PeakConcurrent.Should().BeGreaterThanOrEqualTo(3);
    }

    private sealed class TrackingSleepyTool : ITool
    {
        private readonly int _sleepMs;
        private int _inFlight;
        private int _peak;
        private int _invocations;

        public TrackingSleepyTool(int sleepMs) { _sleepMs = sleepMs; }

        public ToolSchema Schema { get; } = new(
            "Track",
            "test tool that records concurrent runs",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public bool CanRunInParallel => true;
        public int Invocations => Volatile.Read(ref _invocations);
        public int PeakConcurrent => Volatile.Read(ref _peak);

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            // Update peak with a CAS loop — Interlocked.Max would be cleaner if it existed.
            int observedPeak;
            do
            {
                observedPeak = Volatile.Read(ref _peak);
                if (now <= observedPeak) break;
            } while (Interlocked.CompareExchange(ref _peak, now, observedPeak) != observedPeak);

            try
            {
                await Task.Delay(_sleepMs, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _invocations);
                return ToolResult.Success("ok");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
