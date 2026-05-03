using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
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
        // After compaction: system + summary + last 4 user turns and their assistants (8 messages).
        session.Messages.Should().HaveCount(1 + 1 + 8);
        session.Messages[0].Content.Should().Be("you are zdt");
        session.Messages[1].Role.Should().Be("system");
        session.Messages[1].Content.Should().Contain("conversation_summary");
        session.Messages[1].Content.Should().Contain("Synthetic recap");
        session.Messages[2].Content.Should().Be("u3");
        session.Messages[^1].Content.Should().Be("a6");

        // The summarisation request hit the medium model with the compaction system prompt.
        handler.RequestBodies.Should().ContainSingle();
        handler.RequestBodies[0].Should().Contain("\"model\":\"qwen-medium\"");
        handler.RequestBodies[0].Should().Contain("Summarize the following conversation history");
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

        resumed.Messages.Should().HaveCount(1 + 1 + 8);
        resumed.Messages[1].Content.Should().Contain("conversation_summary");
        resumed.Messages[2].Content.Should().Be("u3");
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
}
