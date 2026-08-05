using System.Collections.Immutable;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.Core.Sessions;

public sealed class SessionTests : IDisposable
{
    private readonly string _tempDir;

    public SessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-session-agg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Ephemeral_session_does_not_create_a_file()
    {
        using var session = Session.NewEphemeral("qwen-local", ToolCallingMode.Xml);

        session.Id.Should().NotBeNullOrEmpty();
        session.Model.Should().Be("qwen-local");
        session.Mode.Should().Be(ToolCallingMode.Xml);
        Directory.GetFiles(_tempDir).Should().BeEmpty();
    }

    [Fact]
    public void AddUser_with_images_keeps_them_in_memory_but_persists_only_a_note()
    {
        var store = SessionStore.Create(_tempDir);
        using (var session = Session.NewPersistent(store, "gpt-4o"))
        {
            session.AddUser("describe the picture", new[] { "data:image/png;base64,AAAA" });

            // Live message carries the image so the vision model sees it.
            var msg = session.Messages.Last();
            msg.Role.Should().Be("user");
            msg.Content.Should().Be("describe the picture");
            msg.Images.Should().ContainSingle().Which.Should().Be("data:image/png;base64,AAAA");
        }

        // On disk: the note is persisted, NOT the base64 bytes.
        var text = File.ReadAllText(Directory.GetFiles(_tempDir, "*.jsonl")[0]);
        text.Should().Contain("describe the picture");
        text.Should().Contain("attached 1 image");
        text.Should().NotContain("AAAA");
    }

    [Fact]
    public void AddUser_without_images_is_unchanged()
    {
        using var session = Session.NewEphemeral("m");
        session.AddUser("plain");

        var msg = session.Messages.Last();
        msg.Content.Should().Be("plain");
        msg.Images.Should().BeEmpty();
    }

    [Fact]
    public void Persistent_session_writes_meta_event_immediately()
    {
        var store = SessionStore.Create(_tempDir);
        using var session = Session.NewPersistent(store, "qwen-local", "demo", ToolCallingMode.Xml);

        var events = SessionStore.OpenForResume(_tempDir, store.SessionId).ReadAll().ToList();
        events.Should().ContainSingle();
        var meta = events.OfType<MetaEvent>().Single();
        meta.SessionId.Should().Be(store.SessionId);
        meta.Model.Should().Be("qwen-local");
        meta.Name.Should().Be("demo");
        meta.Mode.Should().Be(ToolCallingMode.Xml);
    }

    [Fact]
    public void Add_methods_mutate_messages_and_persist_events()
    {
        var store = SessionStore.Create(_tempDir);
        using (var session = Session.NewPersistent(store, "m"))
        {
            session.AddSystem("you are zdt");
            session.AddUser("hi");
            session.AddAssistant(
                "calling Read",
                ImmutableArray.Create(new ToolCall("c1", "Read", "{}")));
            session.AddTool("c1", "file contents");
            session.AddAssistant("done", ImmutableArray<ToolCall>.Empty);
            session.AddUsage(10, 20);

            session.Messages.Select(m => m.Role)
                .Should().Equal("system", "user", "assistant", "tool", "assistant");
        }

        // UsageEvent does not appear in Messages but is on disk.
        using var reader = SessionStore.OpenForResume(_tempDir, store.SessionId);
        reader.ReadAll().OfType<UsageEvent>().Single().PromptTokens.Should().Be(10);
    }

    [Fact]
    public void Resume_reconstructs_chat_messages_in_order()
    {
        var store = SessionStore.Create(_tempDir);
        var sessionId = store.SessionId;
        using (var session = Session.NewPersistent(store, "m"))
        {
            session.AddSystem("sys");
            session.AddUser("u1");
            session.AddAssistant("calling Read",
                ImmutableArray.Create(new ToolCall("c1", "Read", "{}")));
            session.AddTool("c1", "result");
            session.AddAssistant("done", ImmutableArray<ToolCall>.Empty);
        }

        using var resumed = Session.Resume(SessionStore.OpenForResume(_tempDir, sessionId));
        resumed.Id.Should().Be(sessionId);
        resumed.Model.Should().Be("m");
        resumed.Messages.Should().HaveCount(5);

        var asst = resumed.Messages[2];
        asst.Role.Should().Be("assistant");
        asst.Content.Should().Be("calling Read");
        asst.ToolCalls.Single().Id.Should().Be("c1");
        asst.ToolCalls.Single().FunctionName.Should().Be("Read");

        resumed.Messages[3].Role.Should().Be("tool");
        resumed.Messages[3].ToolCallId.Should().Be("c1");
    }

    [Fact]
    public void AddAssistant_with_no_content_and_no_tool_calls_gets_a_placeholder()
    {
        // A thinking model can emit only reasoning_content (dropped) with no visible text and no tool
        // call. Persisting/sending that empty assistant turn 400s an OpenAI-compatible server
        // ("must contain either content or tool_calls") and breaks every turn of a resumed session.
        var store = SessionStore.Create(_tempDir);
        var sessionId = store.SessionId;
        using (var session = Session.NewPersistent(store, "m"))
        {
            session.AddUser("hi");
            session.AddAssistant(null, ImmutableArray<ToolCall>.Empty);

            session.Messages.Last().Role.Should().Be("assistant");
            session.Messages.Last().Content.Should().Be("(no response)");
        }

        // And it's persisted with the placeholder, so a later resume is well-formed too.
        File.ReadAllText(Path.Combine(_tempDir, $"{sessionId}.jsonl")).Should().Contain("(no response)");
    }

    [Fact]
    public void Resume_heals_an_empty_assistant_turn_written_by_an_older_build()
    {
        var store = SessionStore.Create(_tempDir);
        var sessionId = store.SessionId;
        using (var session = Session.NewPersistent(store, "m"))
        {
            session.AddSystem("sys");
            session.AddUser("u1");
        }
        // Simulate a degenerate empty assistant turn an older/interrupted build could have persisted.
        var path = Path.Combine(_tempDir, $"{sessionId}.jsonl");
        File.AppendAllText(path, "{\"type\":\"assistant\"}\n");

        using var resumed = Session.Resume(SessionStore.OpenForResume(_tempDir, sessionId));

        var asst = resumed.Messages.Single(m => m.Role == "assistant");
        asst.Content.Should().Be("(no response)", "the empty assistant must be healed so the first post-resume request doesn't 400");
        asst.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public void Resume_preserves_mode_recorded_in_meta()
    {
        var store = SessionStore.Create(_tempDir);
        var sessionId = store.SessionId;
        using (var session = Session.NewPersistent(store, "qwen-local", mode: ToolCallingMode.Xml))
        {
            session.AddSystem("sys");
        }

        using var resumed = Session.Resume(SessionStore.OpenForResume(_tempDir, sessionId));
        resumed.Mode.Should().Be(ToolCallingMode.Xml);
    }

    [Fact]
    public void Resume_throws_when_meta_event_missing()
    {
        var sessionId = Guid.NewGuid().ToString();
        var path = Path.Combine(_tempDir, $"{sessionId}.jsonl");
        // hand-craft a file without a meta event
        File.WriteAllText(path, "{\"type\":\"user\",\"content\":\"hi\"}\n");

        var act = () => Session.Resume(SessionStore.OpenForResume(_tempDir, sessionId));
        act.Should().Throw<InvalidOperationException>().WithMessage("*meta event*");
    }
}
