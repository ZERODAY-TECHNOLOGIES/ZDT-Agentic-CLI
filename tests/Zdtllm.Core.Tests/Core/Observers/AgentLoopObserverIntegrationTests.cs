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
    public async Task Parent_dispatches_three_Agent_tool_calls_in_parallel_without_console_collision()
    {
        // Real-world AppSec pattern: parent emits multiple Agent calls in one turn, AgentLoop
        // fans them out via Task.WhenAll. Pre-3W, each parallel TaskTool opened its own
        // AnsiConsole.Status() spinner — Spectre's interactive lock then threw "Trying to run
        // one or more interactive functions concurrently" and the whole batch crashed.
        // This test reproduces that path end-to-end and asserts the run completes cleanly.
        //
        // Round 1 (parent → 3 Agent tool calls):
        //   one streaming response with three tool_calls at indices 0,1,2.
        // Rounds 2-4 (each subagent → "subagent N done"):
        //   three responses; order in which subagents claim them is non-deterministic but
        //   the end result is the same — each tool returns whichever it dequeued.
        // Round 5 (parent → "all subagents reported"):
        //   the parent sees the three tool_results and finishes.
        var parentRound1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"a1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Agent\",\"arguments\":\"{\\\"description\\\":\\\"w1\\\",\\\"prompt\\\":\\\"p1\\\"}\"}}," +
                "{\"index\":1,\"id\":\"a2\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Agent\",\"arguments\":\"{\\\"description\\\":\\\"w2\\\",\\\"prompt\\\":\\\"p2\\\"}\"}}," +
                "{\"index\":2,\"id\":\"a3\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Agent\",\"arguments\":\"{\\\"description\\\":\\\"w3\\\",\\\"prompt\\\":\\\"p3\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";

        static string SubAnswer(string text) =>
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = text } } },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop" } },
            }) + "\n\n" +
            "data: [DONE]\n\n";

        var parentRound2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"all subagents reported\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(
            Sse(parentRound1),
            Sse(SubAnswer("sub-A")),
            Sse(SubAnswer("sub-B")),
            Sse(SubAnswer("sub-C")),
            Sse(parentRound2));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var registry = new ToolRegistry();
        var parent = new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 5 });
        var runner = new SubagentRunner(parent);
        registry.Register(new TaskTool(runner));

        using var session = Session.NewEphemeral("test-model");
        var result = await parent.RunTurnAsync(
            session, "go", new StringWriter(), new StringWriter());

        result.FinalText.Should().Be("all subagents reported");
        // 1 parent round + 3 subagent rounds + 1 parent round = 5 LLM requests.
        handler.Requests.Should().HaveCount(5);
    }

    [Fact]
    public async Task Parent_passes_session_Model_to_subagent_after_simulated_slash_model_switch()
    {
        // Mid-conversation /model switch: REPL calls session.SetModel("new-model"), then a
        // subsequent turn dispatches an Agent tool. The subagent's HTTP body must use the
        // NEW model — proving ToolContext.Model and SubagentRequest.ParentModel both flow.
        var parentRound1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"a1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Agent\",\"arguments\":\"{\\\"description\\\":\\\"d\\\",\\\"prompt\\\":\\\"p\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var subResponse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"sub done\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var parentRound2 =
            "data: {\"choices\":[{\"delta\":{\"content\":\"finished\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(Sse(parentRound1), Sse(subResponse), Sse(parentRound2));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var registry = new ToolRegistry();
        // AgentLoop is constructed with the OLD model name; the user then runs /model and
        // session.Model becomes "new-model". The subagent's HTTP request should use the new
        // value because AgentLoop populates ctx.Model from session.Model at turn start.
        var parent = new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "old-startup-model", MaxTurns = 5 });
        var runner = new SubagentRunner(parent);
        registry.Register(new TaskTool(runner));

        using var session = Session.NewEphemeral("old-startup-model");
        session.SetModel("new-model"); // simulates /model in the REPL

        await parent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        // Three requests: parent (new-model), subagent (must also be new-model), parent (new-model).
        handler.Requests.Should().HaveCount(3);
        var subagentBody = handler.RequestBodies[1]; // index 1 = the subagent round
        subagentBody.Should().Contain("\"model\":\"new-model\"");
        subagentBody.Should().NotContain("old-startup-model");
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
    public async Task Stream_json_xml_mode_surfaces_tool_use_blocks_and_strips_function_calls_from_text()
    {
        // XML-mode regression: when the model emits <function_calls> in plain text, AgentLoop
        // extracts the calls and runs them — but the stream-json observer must ALSO see them as
        // structured tool_use blocks, not as raw XML in content[].text. Discovered via a Siembiot
        // SAST run where 60/60 assistant events came through with content=[{type:"text",...}]
        // only — AppSec-Automator parses Anthropic's tool_use blocks, so it would have seen
        // zero tool calls despite tools running fine.
        var round1 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content =
                    "I'll echo it.\n<function_calls>\n<invoke name=\"Echo\">\n" +
                    "<parameter name=\"text\">hello</parameter>\n</invoke>\n</function_calls>" } } },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop" } },
            }) + "\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = "all done" } } },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop" } },
            }) + "\n\n" +
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
            new AgentLoopOptions
            {
                Model = "qwen-test",
                MaxTurns = 5,
                ToolCallingMode = ToolCallingMode.Xml,
            },
            observer: observer);

        using var session = Session.NewEphemeral("qwen-test", ToolCallingMode.Xml);
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        events.Select(e => e.GetProperty("type").GetString()).Should().Equal(
            "assistant", "assistant", "result");

        // Iteration 1 must contain a tool_use block (NOT just a text block with raw XML).
        var iter1Content = events[0].GetProperty("message").GetProperty("content");
        var blocks = iter1Content.EnumerateArray().ToList();
        var toolUse = blocks.FirstOrDefault(c => c.GetProperty("type").GetString() == "tool_use");
        toolUse.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "XML-extracted call must be surfaced as a tool_use block");
        toolUse.GetProperty("name").GetString().Should().Be("Echo");
        toolUse.GetProperty("id").GetString().Should().Be("xml_1_0");
        toolUse.GetProperty("input").GetProperty("text").GetString().Should().Be("hello");

        // The text block (if any) MUST NOT contain <function_calls> — that markup is now
        // represented structurally; emitting it raw in text duplicates the payload.
        foreach (var b in blocks.Where(b => b.GetProperty("type").GetString() == "text"))
            b.GetProperty("text").GetString().Should().NotContain("function_calls");

        // Iteration 2: final text only.
        var iter2Content = events[1].GetProperty("message").GetProperty("content");
        iter2Content[0].GetProperty("text").GetString().Should().Be("all done");
    }

    [Fact]
    public async Task Reasoning_content_is_excluded_from_assistant_text_and_session_messages()
    {
        // DeepSeek-V3.2-style stream: model emits reasoning_content first, then content,
        // then a tool call (native), then we round 2 = final text. The observer event for
        // the assistant turn must contain ONLY the content text (not the chain-of-thought),
        // and the next request body sent to the LLM must NOT echo reasoning back as history.
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Let me think about this carefully...\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" I should call Echo with hi.\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"calling tool\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"hi\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2 =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Got the result. Now to summarise.\"}}]}\n\n" +
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
            new AgentLoopOptions { Model = "deepseek-test", MaxTurns = 5 },
            observer: observer);

        using var session = Session.NewEphemeral("deepseek-test");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        // Iteration 1: only the visible content is "calling tool", and the tool_use block.
        var iter1 = events[0].GetProperty("message").GetProperty("content");
        var iter1Text = iter1.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString();
        iter1Text.Should().Be("calling tool",
            "reasoning_content must not appear in the assistant text");
        iter1Text.Should().NotContain("Let me think");
        iter1Text.Should().NotContain("call Echo");

        // Iteration 2: only "all done", no reasoning leak.
        var iter2 = events[1].GetProperty("message").GetProperty("content");
        var iter2Text = iter2.EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString();
        iter2Text.Should().Be("all done");
        iter2Text.Should().NotContain("Got the result");

        // Round 2 request body MUST NOT contain reasoning text (it's ephemeral; sending
        // it back to the model degrades behavior and burns context).
        handler.RequestBodies[1].Should().NotContain("Let me think");
        handler.RequestBodies[1].Should().NotContain("call Echo with hi");
    }

    [Fact]
    public async Task Stream_json_emits_terminal_result_event_when_run_ends_on_empty_completion()
    {
        // Regression: GLM-5.1 / Ollama-Cloud sometimes finish a run by emitting a stream with
        // no content delta and no tool_calls — just `finish_reason:"stop"` and [DONE]. Before
        // the fix the agent loop took the "no tool calls -> exit" branch but the trailing
        // assistant + result events never made it to disk: stream.ndjson stopped right after
        // the previous (tool-bearing) iteration. AppSec-Automator and any other consumer that
        // waits for {"type":"result"} would block / time out on otherwise-successful runs.
        //
        // Round 1: model emits a tool call (this works today). Round 2: stream is content-less
        // (a single finish_reason:stop chunk) — this is the case the regression covers.
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"hi\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var round2_empty =
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHandler(Sse(round1), Sse(round2_empty));
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

        // Two assistant frames (turn 1 with tool_use; turn 2 empty-content) plus the result.
        events.Select(e => e.GetProperty("type").GetString()).Should().Equal(
            "assistant", "assistant", "result");

        // Turn 2's assistant frame: empty content array, no text, no tool_use.
        var turn2Content = events[1].GetProperty("message").GetProperty("content");
        turn2Content.GetArrayLength().Should().Be(0);

        // The terminal result event that was missing pre-fix. subtype=success, stop_reason=
        // end_turn (we treat any no-tool-calls completion as a clean turn-ending — the loop
        // doesn't differentiate empty vs non-empty text).
        var result = events[2];
        result.GetProperty("subtype").GetString().Should().Be("success");
        result.GetProperty("is_error").GetBoolean().Should().BeFalse();
        result.GetProperty("num_turns").GetInt32().Should().Be(2);
        result.GetProperty("stop_reason").GetString().Should().Be("end_turn");
    }

    [Fact]
    public async Task Stream_json_terminal_result_event_is_emitted_after_many_tool_turns_followed_by_empty_completion()
    {
        // Production repro: GLM-5.1 ran a 90-min Network Pentest, emitted 37 tool-bearing
        // turns, then turn 38 came back with finish_reason:"stop" and zero content. The run
        // exited cleanly (exit code 0) but the stream.ndjson file ended at frame 37 with no
        // {"type":"result"} terminal event. AppSec-Automator's StreamJsonResult.php waits
        // for that terminal event and would record the engagement as "no_result" / "engine
        // exited before emitting a final result".
        //
        // Reproduce with 10 tool-bearing rounds + 1 empty closer. We're chasing whether high
        // turn counts shift any state that swallows the terminal emit (reused HttpClient,
        // session message accumulator size, etc.) — the 2-turn variant above passes.
        var responses = new List<HttpResponseMessage>();
        const int toolRounds = 10;
        for (var i = 0; i < toolRounds; i++)
        {
            responses.Add(Sse(
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                    $"{{\"index\":0,\"id\":\"c{i}\",\"type\":\"function\"," +
                    $"\"function\":{{\"name\":\"Echo\",\"arguments\":\"{{\\\"text\\\":\\\"hi{i}\\\"}}\"}}}}]}}}}]}}\n\n" +
                "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
                "data: [DONE]\n\n"));
        }
        responses.Add(Sse(
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n"));

        var handler = new StubHandler(responses.ToArray());
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
            new AgentLoopOptions { Model = "test-model", MaxTurns = toolRounds + 5 },
            observer: observer);

        using var session = Session.NewEphemeral("test-model");
        await agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter());

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        // Expect 11 assistant frames (10 tool rounds + 1 empty closer) + 1 terminal result.
        events.Count.Should().Be(toolRounds + 2);
        events.Last().GetProperty("type").GetString().Should().Be("result");
        events.Last().GetProperty("subtype").GetString().Should().Be("success");
        events.Last().GetProperty("num_turns").GetInt32().Should().Be(toolRounds + 1);
    }

    [Fact]
    public async Task Stream_json_emits_terminal_result_event_with_cancelled_stop_reason_when_run_is_cancelled()
    {
        // Regression: when zdt is cancelled mid-stream — Ctrl+C, SIGTERM (zdt now hooks
        // PosixSignalRegistration so `timeout` / k8s kill don't drop the terminal frame),
        // or any orchestrator-initiated cancellation — the AgentLoop's catch
        // (OperationCanceledException) branch is responsible for writing exactly one
        // terminal {"type":"result","stop_reason":"cancelled"} stream-json event before
        // rethrowing. AppSec-Automator parses that to mark the engagement as cancelled
        // (vs "no_result" — engine exited mid-action). Pin it with a real cancellation.
        //
        // We cancel between turn 1 (tool call) and turn 2 (would be the model's follow-up)
        // by using a CancellationTokenSource that the test cancels as soon as the LLM
        // request for round 2 is made.
        var round1 =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                "{\"index\":0,\"id\":\"c1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"Echo\",\"arguments\":\"{\\\"text\\\":\\\"hi\\\"}\"}}]}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";

        // Round 2 hangs forever — simulates an in-flight LLM call that gets cancelled.
        var infiniteResponses = new HttpResponseMessage[]
        {
            Sse(round1),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream()),
            },
        };
        infiniteResponses[1].Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        var handler = new StubHandler(infiniteResponses);
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
        using var cts = new CancellationTokenSource();

        // Cancel after the first round's tool result is processed but before the second
        // round can produce any output. 200 ms is generous — round 1 completes near-instantly.
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var run = agent.RunTurnAsync(session, "go", new StringWriter(), new StringWriter(), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var events = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        // The very last event must be the terminal result, even though the run was killed.
        var result = events.Last();
        result.GetProperty("type").GetString().Should().Be("result");
        result.GetProperty("subtype").GetString().Should().Be("error_during_execution");
        result.GetProperty("is_error").GetBoolean().Should().BeTrue();
        result.GetProperty("stop_reason").GetString().Should().Be("cancelled");
    }

    /// <summary>HTTP stream that blocks forever on read. Used to simulate an in-flight LLM call
    /// that the agent loop cancels mid-stream.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0; // unreachable — Task.Delay throws on cancellation
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
