using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Loop-detection guards inside <see cref="AgentLoop"/>: catch the model spinning on the
/// same (tool, args) or rotating arguments through the same result. The detector is
/// advisory — it injects guidance into the tool result the model sees rather than
/// killing the run — so these tests assert on what the model RECEIVES (the tool's
/// return value visible to it) rather than on cancelled exceptions.
///
/// All tests drive RunOneShotAsync with stubbed SSE rounds so the dispatcher path is
/// exercised end-to-end (CheckExactRepeat, EnqueueTrace, CheckPermutationLoop).
/// </summary>
public sealed class AgentLoopLoopDetectionTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static string ToolCallRound(int id, string toolName, string argsJson)
    {
        // Native-mode SSE delta carrying one tool call. Args go through the model's
        // normal JSON-stringification path. Built without string interpolation to keep
        // the brace count humanly verifiable — the existing AgentLoopTests use this
        // exact form (verbatim concat of escaped chunks).
        var escapedArgs = argsJson.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
            "{\"index\":0,\"id\":\"c" + id + "\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"" + toolName + "\",\"arguments\":\"" + escapedArgs + "\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
    }

    private static string FinalTextRound(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private static AgentLoop BuildAgent(StubHandler handler, ToolRegistry registry, int maxTurns = 30)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub",
            ApiKey = "k",
            MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = maxTurns });
    }

    [Fact]
    public async Task ExactRepeat_blocks_third_identical_call_with_loop_break_message()
    {
        // Model emits the same Echo call 3 times in a row, then quits. Calls 1 and 2
        // execute normally; call 3 must be short-circuited (Invocations stays at 2)
        // and the model receives a [loop-break] tool result instead of the echoed text.
        var args = """{"text":"x"}""";
        var handler = new StubHandler(
            Sse(ToolCallRound(1, "Echo", args)),
            Sse(ToolCallRound(2, "Echo", args)),
            Sse(ToolCallRound(3, "Echo", args)),
            Sse(FinalTextRound("done")));

        var registry = new ToolRegistry();
        var echo = new RecordingTool("Echo", _ => "echoed:x");
        registry.Register(echo);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("loop", new StringWriter(), new StringWriter());

        echo.Invocations.Should().Be(2,
            "the third identical call must be blocked before it reaches the tool");

        // The third request body is what the model received as input for its 4th turn —
        // it should contain the loop-break message rather than 'echoed:x'.
        handler.RequestBodies[3].Should().Contain("loop-break");
        handler.RequestBodies[3].Should().Contain("identical arguments");
    }

    [Fact]
    public async Task ExactRepeat_does_not_block_when_result_changed_between_calls_read_after_write_path()
    {
        // Mutating tool: each invocation flips its return value. Models reading the same
        // file before and after a write legitimately see different results — the detector
        // must allow this to continue.
        var responses = new[] { "v1", "v2", "v3", "v4", "v5" };
        var idx = 0;
        var args = """{"file_path":"a.cs"}""";
        var handler = new StubHandler(
            Sse(ToolCallRound(1, "Read", args)),
            Sse(ToolCallRound(2, "Read", args)),
            Sse(ToolCallRound(3, "Read", args)),
            Sse(ToolCallRound(4, "Read", args)),
            Sse(FinalTextRound("done")));

        var registry = new ToolRegistry();
        var read = new RecordingTool("Read", _ => responses[idx++]);
        registry.Register(read);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("verify edits", new StringWriter(), new StringWriter());

        read.Invocations.Should().Be(4,
            "every Read returned a different value, so the detector must not short-circuit");
        // None of the responses fed back to the model should mention loop-break.
        for (var i = 1; i < handler.RequestBodies.Count; i++)
            handler.RequestBodies[i].Should().NotContain("loop-break");
    }

    [Fact]
    public async Task ExactRepeat_resets_run_after_intermediate_state_change()
    {
        // 1 read of "v1" (initial state), then 3 reads returning "v2" (post-edit). The
        // state-change boundary at v1→v2 inside the buffer breaks the trailing-same
        // scan: calls 2 and 3 (both "v2") are allowed; call 4 (third consecutive "v2")
        // fires the threshold. Without the state-change reset semantics, call 2 would
        // be blocked too — proving the detector respects observed transitions.
        var responses = new[] { "v1", "v2", "v2", "v2" };
        var idx = 0;
        var args = """{"file_path":"a.cs"}""";
        var handler = new StubHandler(
            Sse(ToolCallRound(1, "Read", args)),
            Sse(ToolCallRound(2, "Read", args)),
            Sse(ToolCallRound(3, "Read", args)),
            Sse(ToolCallRound(4, "Read", args)),
            Sse(FinalTextRound("done")));

        var registry = new ToolRegistry();
        var read = new RecordingTool("Read", _ => responses[idx++]);
        registry.Register(read);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("loop", new StringWriter(), new StringWriter());

        read.Invocations.Should().Be(3,
            "v1 + v2 + v2 succeed; only the 4th read (third consecutive v2) is blocked");
    }

    [Fact]
    public async Task PermutationLoop_fires_on_grep_with_three_distinct_patterns_same_result()
    {
        // Three Grep calls with permuted args, each returning the canonical "(no
        // matches)" — the post-execute permutation check should append a loop-break
        // hint to the third call's result.
        var handler = new StubHandler(
            Sse(ToolCallRound(1, "Grep", """{"pattern":"foo"}""")),
            Sse(ToolCallRound(2, "Grep", """{"pattern":"bar"}""")),
            Sse(ToolCallRound(3, "Grep", """{"pattern":"baz"}""")),
            Sse(FinalTextRound("done")));

        var registry = new ToolRegistry();
        var grep = new RecordingTool("Grep", _ => "(no matches)");
        registry.Register(grep);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("search", new StringWriter(), new StringWriter());

        grep.Invocations.Should().Be(3,
            "permutation check is post-execute — it warns but doesn't short-circuit");

        // The body of the 4th request (the model's view of the 3rd Grep's result) must
        // contain the loop-break suffix; the 1st and 2nd responses must not.
        handler.RequestBodies[1].Should().NotContain("loop-break");
        handler.RequestBodies[2].Should().NotContain("loop-break");
        handler.RequestBodies[3].Should().Contain("loop-break");
        handler.RequestBodies[3].Should().Contain("permuting");
    }

    [Fact]
    public async Task PermutationLoop_does_not_fire_on_read_when_results_share_prefix()
    {
        // Read is intentionally exempt from the permutation check — reading 5 stub
        // Migration_*.cs files where the headers are identical is a normal pattern,
        // not a loop. The detector must not punish it.
        var handler = new StubHandler(
            Sse(ToolCallRound(1, "Read", """{"file_path":"M_001.cs"}""")),
            Sse(ToolCallRound(2, "Read", """{"file_path":"M_002.cs"}""")),
            Sse(ToolCallRound(3, "Read", """{"file_path":"M_003.cs"}""")),
            Sse(ToolCallRound(4, "Read", """{"file_path":"M_004.cs"}""")),
            Sse(FinalTextRound("done")));

        var registry = new ToolRegistry();
        var read = new RecordingTool("Read", _ => "// auto-generated stub");
        registry.Register(read);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("read all", new StringWriter(), new StringWriter());

        read.Invocations.Should().Be(4);
        for (var i = 1; i < handler.RequestBodies.Count; i++)
            handler.RequestBodies[i].Should().NotContain("loop-break");
    }

    [Fact]
    public async Task ExactRepeat_detects_calls_when_only_json_key_order_differs()
    {
        // Models routinely rotate JSON key order between native and XML mode. The hash
        // normaliser sorts keys before fingerprinting, so {"a":1,"b":2} must collide
        // with {"b":2,"a":1} — otherwise loop detection misses half the cases.
        var handler = new StubHandler(
            Sse(ToolCallRound(1, "Echo", """{"a":"x","b":"y"}""")),
            Sse(ToolCallRound(2, "Echo", """{"b":"y","a":"x"}""")),
            Sse(ToolCallRound(3, "Echo", """{"a":"x","b":"y"}""")),
            Sse(FinalTextRound("done")));

        var registry = new ToolRegistry();
        var echo = new RecordingTool("Echo", _ => "always-same");
        registry.Register(echo);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("loop with rotation", new StringWriter(), new StringWriter());

        echo.Invocations.Should().Be(2,
            "the third call should be blocked despite key-order rotation");
        handler.RequestBodies[3].Should().Contain("loop-break");
    }

    [Fact]
    public async Task LoopState_resets_between_run_turn_async_calls()
    {
        // Two separate RunTurnAsync calls on the same agent: state from the first
        // must NOT influence the second.
        var args = """{"text":"x"}""";
        var handler = new StubHandler(
            // Turn 1: 3 identical calls → 3rd blocked, then final text.
            Sse(ToolCallRound(1, "Echo", args)),
            Sse(ToolCallRound(2, "Echo", args)),
            Sse(ToolCallRound(3, "Echo", args)),
            Sse(FinalTextRound("turn1-done")),
            // Turn 2: 1 Echo call, then final text. Without reset, the buffer would
            // still hold the 3 traces from turn 1 and this Echo would be blocked.
            Sse(ToolCallRound(4, "Echo", args)),
            Sse(FinalTextRound("turn2-done")));

        var registry = new ToolRegistry();
        var echo = new RecordingTool("Echo", _ => "echoed:x");
        registry.Register(echo);

        var agent = BuildAgent(handler, registry);

        var session = Zdtllm.Core.Sessions.Session.NewEphemeral("test-model", ToolCallingMode.Native);
        await agent.RunTurnAsync(session, "first", new StringWriter(), new StringWriter());
        echo.Invocations.Should().Be(2);

        await agent.RunTurnAsync(session, "second", new StringWriter(), new StringWriter());
        echo.Invocations.Should().Be(3,
            "loop state must reset per RunTurnAsync — the second turn's Echo should execute");
    }

    [Fact]
    public async Task ConsecutiveLoopBreaks_reaching_max_appends_final_exit_directive()
    {
        // Manually drive enough breaks to trip MaxConsecutiveBreaks. Three identical
        // calls produce one break (the 3rd is blocked); we need the model to keep
        // emitting the same call until 3 breaks accumulate. After that, every break
        // message also carries the [loop-break-final] directive.
        var args = """{"text":"x"}""";
        var rounds = new List<HttpResponseMessage>();
        // 6 identical calls: calls 1-2 execute, calls 3-6 are blocked → 4 breaks total,
        // and the 4th, 5th, 6th break responses must include the final exit directive.
        for (var i = 1; i <= 6; i++) rounds.Add(Sse(ToolCallRound(i, "Echo", args)));
        rounds.Add(Sse(FinalTextRound("done")));
        var handler = new StubHandler(rounds.ToArray());

        var registry = new ToolRegistry();
        var echo = new RecordingTool("Echo", _ => "echoed:x");
        registry.Register(echo);

        var agent = BuildAgent(handler, registry);
        await agent.RunOneShotAsync("hammer", new StringWriter(), new StringWriter());

        echo.Invocations.Should().Be(2);

        // The 4th, 5th, 6th tool-result bodies (corresponding to the 3rd, 4th, 5th, 6th
        // blocked calls) — at least the later ones — must include the final exit directive.
        var lastBody = handler.RequestBodies[^1];
        lastBody.Should().Contain("loop-break-final");
        lastBody.Should().Contain("Stop calling tools");
    }

    /// <summary>
    /// Tool stub that records each invocation and returns whatever the supplied factory
    /// produces. Lets each test control return values per-call (verify-after-edit) or
    /// hold them constant (loop scenarios).
    /// </summary>
    private sealed class RecordingTool : ITool
    {
        private readonly string _name;
        private readonly Func<JsonElement, string> _producer;

        public RecordingTool(string name, Func<JsonElement, string> producer)
        {
            _name = name;
            _producer = producer;
        }

        public int Invocations { get; private set; }

        public ToolSchema Schema => new(
            _name,
            $"{_name} test stub.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new Dictionary<string, object>(),
            }));

        public string? GetSpecifierForPermissions(JsonElement args) => null;

        public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(ToolResult.Success(_producer(args)));
        }
    }
}
