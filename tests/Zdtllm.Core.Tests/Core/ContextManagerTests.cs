using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.Core;

public sealed class ContextManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ContextManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static HttpResponseMessage CompletionResponse(string text)
    {
        var sse =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = text } } },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop" } },
            }) + "\n\n" +
            "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
    }

    private static LiteLLMClient BuildClient(StubHandler handler) =>
        new(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public void RegisterTurn_updates_usage_metrics()
    {
        var ctx = new ContextManager(contextWindow: 1000, mediumModel: "med");

        ctx.RegisterTurn(promptTokens: 250, completionTokens: 30);

        ctx.LastPromptTokens.Should().Be(250);
        ctx.LastCompletionTokens.Should().Be(30);
        ctx.UsagePercent.Should().Be(25);
        ctx.IsBeyondSoftThreshold.Should().BeFalse();
        ctx.IsBeyondHardThreshold.Should().BeFalse();
    }

    [Fact]
    public void Soft_threshold_fires_at_or_above_80_percent_default()
    {
        var ctx = new ContextManager(1000, "med");

        ctx.RegisterTurn(799, 0);
        ctx.IsBeyondSoftThreshold.Should().BeFalse();

        ctx.RegisterTurn(800, 0);
        ctx.IsBeyondSoftThreshold.Should().BeTrue();
        ctx.IsBeyondHardThreshold.Should().BeFalse();
    }

    [Fact]
    public void Hard_threshold_fires_at_or_above_90_percent_default()
    {
        var ctx = new ContextManager(1000, "med");

        ctx.RegisterTurn(900, 0);
        ctx.IsBeyondHardThreshold.Should().BeTrue();
    }

    [Fact]
    public void Projected_threshold_catches_tool_results_added_after_last_usage_chunk()
    {
        // The scenario the AgentLoop's auto-compact has to defend against: a turn read several
        // big files, the server reported moderate prompt_tokens for the iteration that produced
        // the tool calls, but the tool results just appended push the NEXT prompt over budget.
        // RegisterTurn-based IsBeyondHardThreshold says "fine" (we're under 90% based on last
        // server count), but the projected check on the session size says "compact now".
        var ctx = new ContextManager(contextWindow: 1_000, mediumModel: "med");

        // Pretend the last server roundtrip saw 500 prompt tokens — under the 0.9 threshold.
        ctx.RegisterTurn(500, 50);
        ctx.IsBeyondHardThreshold.Should().BeFalse();

        // Build a session whose total estimated content is ~1100 tokens (well over 0.9 × 1000).
        // With chars/4 token estimation, we need ~4400 chars across messages.
        using var session = Session.NewEphemeral("test");
        session.AddSystem(new string('s', 200));                          // ~50 tok
        session.AddUser(new string('q', 200));                            // ~50 tok
        session.AddAssistant("ok", ImmutableArray<ToolCall>.Empty);        // ~1 tok
        session.AddTool("call-1", new string('x', 4_000));                // ~1000 tok — the big file we just read

        ctx.IsProjectedBeyondHardThreshold(session).Should().BeTrue(
            "tool results just landed in the session, so the next iteration's prompt would blow the window");
    }

    [Fact]
    public void Projected_threshold_says_false_when_session_is_well_under_budget()
    {
        var ctx = new ContextManager(contextWindow: 10_000, mediumModel: "med");
        using var session = Session.NewEphemeral("test");
        session.AddSystem("sys");
        session.AddUser("hello");
        session.AddAssistant("hi", ImmutableArray<ToolCall>.Empty);

        ctx.IsProjectedBeyondHardThreshold(session).Should().BeFalse();
    }

    [Fact]
    public void Slice_returns_empty_body_when_fewer_than_5_user_turns()
    {
        // 4 user turns: nothing eligible to compact yet.
        var msgs = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("u1"), ChatMessage.AssistantText("a1"),
            ChatMessage.User("u2"), ChatMessage.AssistantText("a2"),
            ChatMessage.User("u3"), ChatMessage.AssistantText("a3"),
            ChatMessage.User("u4"), ChatMessage.AssistantText("a4"),
        };

        var (head, body, tail) = ContextManager.Slice(msgs);

        head.Should().BeEmpty();
        body.Should().BeEmpty();
        tail.Should().BeEquivalentTo(msgs);
    }

    [Fact]
    public void Slice_keeps_system_head_and_last_four_user_turns()
    {
        var msgs = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("u1"), ChatMessage.AssistantText("a1"),
            ChatMessage.User("u2"), ChatMessage.AssistantText("a2"),
            ChatMessage.User("u3"), ChatMessage.AssistantText("a3"),
            ChatMessage.User("u4"), ChatMessage.AssistantText("a4"),
            ChatMessage.User("u5"), ChatMessage.AssistantText("a5"),
            ChatMessage.User("u6"), ChatMessage.AssistantText("a6"),
        };

        var (head, body, tail) = ContextManager.Slice(msgs);

        head.Single().Content.Should().Be("sys");
        // Body covers u1+a1+u2+a2 (the two oldest user turns).
        body.Should().HaveCount(4);
        body[0].Content.Should().Be("u1");
        body[3].Content.Should().Be("a2");
        // Tail covers u3..a6 — the last 4 user turns and their assistants.
        tail.Should().HaveCount(8);
        tail[0].Content.Should().Be("u3");
        tail[^1].Content.Should().Be("a6");
    }

    [Fact]
    public async Task CompactAsync_calls_medium_model_and_replaces_history_with_summary()
    {
        var store = SessionStore.Create(_tempDir);
        using var session = Session.NewPersistent(store, "any-model");
        session.AddSystem("you are zdt");
        for (var i = 1; i <= 6; i++)
        {
            session.AddUser($"u{i}");
            session.AddAssistant($"a{i}", ImmutableArray<ToolCall>.Empty);
        }

        var handler = new StubHandler(CompletionResponse("Synthetic recap of u1..u2."));
        var client = BuildClient(handler);
        var ctx = new ContextManager(contextWindow: 100_000, mediumModel: "qwen-medium");

        var collapsed = await ctx.CompactAsync(session, client);

        collapsed.Should().Be(4); // u1+a1+u2+a2
        // After compaction: ONE leading system message (prompt + summary folded together, so the
        // Qwen/GLM "system must be first" template constraint holds) + the last 4 user turns and
        // their assistants (8 messages).
        session.Messages.Should().HaveCount(1 + 8);
        session.Messages.Count(m => m.Role == "system").Should().Be(1);
        session.Messages[0].Role.Should().Be("system");
        session.Messages[0].Content.Should().Contain("you are zdt");
        session.Messages[0].Content.Should().Contain("conversation_summary");
        session.Messages[0].Content.Should().Contain("Synthetic recap");
        session.Messages[1].Content.Should().Be("u3");
        session.Messages[^1].Content.Should().Be("a6");

        // The summarisation request hit the medium model with the compaction system prompt.
        handler.RequestBodies.Should().ContainSingle();
        handler.RequestBodies[0].Should().Contain("\"model\":\"qwen-medium\"");
        handler.RequestBodies[0].Should().Contain("Summarize the following conversation history");
    }

    [Fact]
    public async Task Repeated_compaction_does_not_stack_summary_blocks()
    {
        // The "context creeps up after each compact" bug: each compaction used to APPEND a new
        // <conversation_summary> to the system prompt, so N compactions = N stacked blocks. Now the
        // prior block is stripped and folded into the new one → always exactly one block.
        var store = SessionStore.Create(_tempDir);
        using var session = Session.NewPersistent(store, "m");
        session.AddSystem("you are zdt");
        for (var i = 1; i <= 6; i++) { session.AddUser($"u{i}"); session.AddAssistant($"a{i}", ImmutableArray<ToolCall>.Empty); }

        var handler = new StubHandler(CompletionResponse("RECAP-ONE"), CompletionResponse("RECAP-TWO"));
        var client = BuildClient(handler);
        var ctx = new ContextManager(100_000, "qwen-medium");

        await ctx.CompactAsync(session, client); // 1st: folds RECAP-ONE into the system prompt
        for (var i = 7; i <= 12; i++) { session.AddUser($"u{i}"); session.AddAssistant($"a{i}", ImmutableArray<ToolCall>.Empty); }
        await ctx.CompactAsync(session, client); // 2nd: must REPLACE the old block, not stack a second

        var system = session.Messages[0];
        system.Role.Should().Be("system");
        Regex.Matches(system.Content!, "<conversation_summary>").Count.Should().Be(1); // exactly ONE block
        system.Content.Should().Contain("you are zdt");   // original prompt survives every pass
        system.Content.Should().Contain("RECAP-TWO");      // newest summary is the one kept
        system.Content.Should().NotContain("RECAP-ONE");   // the old block was folded in, not appended
        // ...and the prior summary was carried into the 2nd summarisation request so nothing is lost.
        handler.RequestBodies[^1].Should().Contain("RECAP-ONE");
    }

    [Fact]
    public async Task CompactAsync_returns_zero_when_nothing_eligible()
    {
        var store = SessionStore.Create(_tempDir);
        using var session = Session.NewPersistent(store, "m");
        session.AddSystem("sys");
        session.AddUser("u1"); session.AddAssistant("a1");

        var handler = new StubHandler();
        var client = BuildClient(handler);
        var ctx = new ContextManager(1_000, "qwen-medium");

        var collapsed = await ctx.CompactAsync(session, client);

        collapsed.Should().Be(0);
        handler.Requests.Should().BeEmpty();
        session.Messages.Should().HaveCount(3); // unchanged
    }

    [Fact]
    public async Task After_compaction_resume_rebuilds_from_the_snapshot()
    {
        var store = SessionStore.Create(_tempDir);
        var sessionId = store.SessionId;

        using (var session = Session.NewPersistent(store, "m"))
        {
            session.AddSystem("sys");
            for (var i = 1; i <= 6; i++)
            {
                session.AddUser($"u{i}");
                session.AddAssistant($"a{i}", ImmutableArray<ToolCall>.Empty);
            }

            var handler = new StubHandler(CompletionResponse("Recap."));
            var client = BuildClient(handler);
            var ctx = new ContextManager(100_000, "qwen-medium");
            await ctx.CompactAsync(session, client);
        }

        using var resumed = Session.Resume(SessionStore.OpenForResume(_tempDir, sessionId));

        resumed.Messages.Should().HaveCount(1 + 8);
        resumed.Messages.Count(m => m.Role == "system").Should().Be(1);
        resumed.Messages[0].Content.Should().Contain("conversation_summary");
        resumed.Messages[1].Content.Should().Be("u3");
    }

    [Fact]
    public void Resume_heals_an_older_double_system_compaction_into_one_leading_system()
    {
        // A build before the "system must be first" fix persisted the summary as a SECOND system
        // message. Resume must coalesce the two so the replayed history is valid to send again.
        var store = SessionStore.Create(_tempDir);
        var sessionId = store.SessionId;

        using (var session = Session.NewPersistent(store, "m"))
        {
            session.AddSystem("base prompt");
            session.AddSystem("<conversation_summary>\nrecap\n</conversation_summary>"); // old broken shape
            session.AddUser("hi again");
        }

        using var resumed = Session.Resume(SessionStore.OpenForResume(_tempDir, sessionId));

        resumed.Messages.Count(m => m.Role == "system").Should().Be(1);
        resumed.Messages[0].Role.Should().Be("system");
        resumed.Messages[0].Content.Should().Contain("base prompt");
        resumed.Messages[0].Content.Should().Contain("conversation_summary");
        resumed.Messages[1].Content.Should().Be("hi again");
    }

    [Fact]
    public void EstimateTokensByRole_groups_by_role_via_4_chars_per_token()
    {
        using var session = Session.NewEphemeral("m");
        session.AddSystem("aaaa");                   // 4 chars → 1 token
        session.AddUser("bbbbbbbb");                  // 8 chars → 2 tokens
        session.AddAssistant("cccccccccccc");         // 12 chars → 3 tokens
        session.AddTool("call_1", "dddd");            // 4 + 6 (id) = 10 chars → 3 tokens

        var byRole = ContextManager.EstimateTokensByRole(session);

        byRole["system"].Should().Be(1);
        byRole["user"].Should().Be(2);
        byRole["assistant"].Should().Be(3);
        byRole["tool"].Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void EstimateTokensByRole_counts_assistant_tool_call_arguments_too()
    {
        using var session = Session.NewEphemeral("m");
        session.AddAssistant(
            content: null,
            toolCalls: ImmutableArray.Create(
                new ToolCall("c1", "Read", "{\"path\":\"./README.md\"}")));

        var byRole = ContextManager.EstimateTokensByRole(session);

        byRole["assistant"].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CompactAsync_resets_LastPromptTokens_so_warnings_stop()
    {
        var store = SessionStore.Create(_tempDir);
        using var session = Session.NewPersistent(store, "m");
        session.AddSystem("sys");
        for (var i = 1; i <= 6; i++)
        {
            session.AddUser($"u{i}"); session.AddAssistant($"a{i}");
        }

        var ctx = new ContextManager(1_000, "qwen-medium");
        ctx.RegisterTurn(promptTokens: 950, completionTokens: 0);
        ctx.IsBeyondHardThreshold.Should().BeTrue();

        var handler = new StubHandler(CompletionResponse("recap"));
        var client = BuildClient(handler);
        await ctx.CompactAsync(session, client);

        ctx.LastPromptTokens.Should().Be(0);
        ctx.IsBeyondHardThreshold.Should().BeFalse();
    }

    // ── TruncateOldToolResults: the in-turn fallback for the single-long-turn / GLM pattern ──

    [Fact]
    public void TruncateOldToolResults_shortens_old_results_and_keeps_recent_verbatim()
    {
        using var session = Session.NewEphemeral("m");
        var ctx = new ContextManager(contextWindow: 100_000, mediumModel: "med");

        // The GLM sweet spot: ONE user turn, then many assistant⇄tool rounds with big tool output.
        session.AddSystem("sys");
        session.AddUser("do the big task");
        for (var i = 0; i < 6; i++)
        {
            session.AddAssistant(null, ImmutableArray.Create(new ToolCall($"c{i}", "Read", "{}")));
            session.AddTool($"c{i}", new string((char)('a' + i), 6_000)); // 6 KB each
        }

        var before = session.Messages.Count;
        var truncated = ctx.TruncateOldToolResults(session, keepLastToolResults: 3, perResultCap: 2_000);

        truncated.Should().Be(3);                      // the three oldest of six
        session.Messages.Count.Should().Be(before);    // truncation never drops messages (pairing intact)

        var toolMsgs = session.Messages.Where(m => m.Role == "tool").ToList();
        toolMsgs.Should().HaveCount(6);
        toolMsgs.Take(3).Should().OnlyContain(m => m.Content!.Contains("truncated") && m.Content!.Length < 6_000);
        toolMsgs.Skip(3).Should().OnlyContain(m => m.Content!.Length == 6_000); // freshest kept verbatim
    }

    [Fact]
    public void TruncateOldToolResults_also_shortens_xml_mode_tool_result_user_turns()
    {
        // XML tool-calling feeds results back as synthetic user turns ("EXECUTION RESULT of [Tool]:"),
        // not role="tool". Truncation must treat those the same, or a tool-heavy XML-mode session
        // accumulates unbounded output (as user turns) and blows the context window.
        using var session = Session.NewEphemeral("m");
        var ctx = new ContextManager(contextWindow: 100_000, mediumModel: "med");

        session.AddSystem("sys");
        session.AddUser("do the big task");
        for (var i = 0; i < 6; i++)
        {
            session.AddAssistant($"<function_calls> read {i}", ImmutableArray<ToolCall>.Empty);
            session.AddUser("EXECUTION RESULT of [Read]:\n" + new string((char)('a' + i), 6_000));
        }

        var truncated = ctx.TruncateOldToolResults(session, keepLastToolResults: 3, perResultCap: 2_000);

        truncated.Should().Be(3); // the three oldest of the six XML tool-result turns
        var toolTurns = session.Messages
            .Where(m => m.Role == "user" && m.Content!.StartsWith("EXECUTION RESULT of [", StringComparison.Ordinal))
            .ToList();
        toolTurns.Should().HaveCount(6);
        toolTurns.Take(3).Should().OnlyContain(m => m.Content!.Contains("truncated") && m.Content!.Length < 6_000);
        toolTurns.Skip(3).Should().OnlyContain(m => m.Content!.Length >= 6_000); // freshest kept verbatim
    }

    [Fact]
    public void TruncateOldToolResults_is_a_noop_when_too_few_tool_results()
    {
        using var session = Session.NewEphemeral("m");
        var ctx = new ContextManager(1_000, "med");
        session.AddUser("u");
        session.AddAssistant(null, ImmutableArray.Create(new ToolCall("c1", "Read", "{}")));
        session.AddTool("c1", new string('x', 9_000));

        ctx.TruncateOldToolResults(session, keepLastToolResults: 3).Should().Be(0);
        session.Messages.Single(m => m.Role == "tool").Content!.Length.Should().Be(9_000);
    }

    private static Session BigSingleTurnSession()
    {
        // One user turn, then 6 assistant⇄tool rounds with 16 KB of tool output each (~24k tokens
        // total) — the single-long-turn shape where summarisation frees nothing.
        var session = Session.NewEphemeral("m");
        session.AddSystem("sys");
        session.AddUser("do the big task");
        for (var i = 0; i < 6; i++)
        {
            session.AddAssistant(null, ImmutableArray.Create(new ToolCall($"c{i}", "Read", "{}")));
            session.AddTool($"c{i}", new string((char)('a' + i), 16_000));
        }
        return session;
    }

    [Fact]
    public async Task CompactToFitAsync_escalates_truncation_to_get_a_single_long_turn_under_threshold()
    {
        var ctx = new ContextManager(contextWindow: 10_000, mediumModel: "med"); // hard threshold ≈ 9k tokens

        // A single fixed-cap pass is NOT enough: the newest results kept verbatim still overflow.
        using (var oneShot = BigSingleTurnSession())
        {
            ctx.TruncateOldToolResults(oneShot, keepLastToolResults: 3, perResultCap: 2_000);
            ctx.IsProjectedBeyondHardThreshold(oneShot)
                .Should().BeTrue("one truncation pass at a fixed cap leaves the big recent results in place");
        }

        // CompactToFitAsync escalates (keep 3→2→1, cap 2000→1000→500) until under the threshold.
        using var session = BigSingleTurnSession();
        ctx.IsProjectedBeyondHardThreshold(session).Should().BeTrue();

        // One user turn short-circuits summarisation, so the client is never called.
        var client = BuildClient(new StubHandler());
        var freed = await ctx.CompactToFitAsync(session, client);

        freed.Should().BeGreaterThan(0);
        ctx.IsProjectedBeyondHardThreshold(session)
            .Should().BeFalse("escalating truncation brings a single long turn back under the window");
        session.Messages.Last(m => m.Role == "tool").Content!.Length
            .Should().Be(16_000, "the newest tool result stays verbatim even at the most aggressive level");
    }
}
