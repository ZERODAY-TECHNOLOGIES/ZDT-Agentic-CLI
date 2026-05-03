using Zdtllm.Core;
using Zdtllm.Core.Sessions;

namespace Zdtllm.Core.Tests.Core.Sessions;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Create_assigns_a_uuid_and_writes_to_session_file()
    {
        using var store = SessionStore.Create(_tempDir);

        store.SessionId.Should().NotBeNullOrEmpty();
        Guid.TryParse(store.SessionId, out _).Should().BeTrue();

        store.Append(new MetaEvent(store.SessionId, "qwen-local"));

        File.Exists(store.Path).Should().BeTrue();
        store.Path.Should().EndWith($"{store.SessionId}.jsonl");
    }

    [Fact]
    public void Round_trip_events_preserves_order_and_payload()
    {
        var sessionId = Guid.NewGuid().ToString();
        using (var store = SessionStore.Create(_tempDir, sessionId))
        {
            store.Append(new MetaEvent(sessionId, "qwen-local", "my-session", ToolCallingMode.Xml));
            store.Append(new SystemEvent("you are zdt"));
            store.Append(new UserEvent("hi"));
            store.Append(new AssistantEvent(
                "I'll help.",
                new[] { new ToolCallEvent("c1", "Read", "{\"path\":\"./x\"}") }));
            store.Append(new ToolEvent("c1", "file contents"));
            store.Append(new UsageEvent(50, 12));
        }

        using var reader = SessionStore.OpenForResume(_tempDir, sessionId);
        var events = reader.ReadAll().ToList();

        events.Should().HaveCount(6);
        events[0].Should().BeOfType<MetaEvent>().Which.Mode.Should().Be(ToolCallingMode.Xml);
        events[0].Should().BeOfType<MetaEvent>().Which.Name.Should().Be("my-session");
        events[1].Should().BeOfType<SystemEvent>().Which.Content.Should().Be("you are zdt");
        events[2].Should().BeOfType<UserEvent>().Which.Content.Should().Be("hi");
        var assistant = events[3].Should().BeOfType<AssistantEvent>().Subject;
        assistant.Content.Should().Be("I'll help.");
        assistant.ToolCalls!.Single().Should().BeEquivalentTo(
            new ToolCallEvent("c1", "Read", "{\"path\":\"./x\"}"));
        events[4].Should().BeOfType<ToolEvent>().Which.ToolCallId.Should().Be("c1");
        var usage = events[5].Should().BeOfType<UsageEvent>().Subject;
        usage.PromptTokens.Should().Be(50);
        usage.CompletionTokens.Should().Be(12);
    }

    [Fact]
    public void Polymorphic_discriminator_appears_first_in_JSON()
    {
        var sessionId = Guid.NewGuid().ToString();
        using (var store = SessionStore.Create(_tempDir, sessionId))
        {
            store.Append(new UserEvent("hello"));
        }

        var path = Path.Combine(_tempDir, $"{sessionId}.jsonl");
        var line = File.ReadAllLines(path)[0];
        line.Should().StartWith("{\"type\":\"user\"");
        line.Should().Contain("\"content\":\"hello\"");
    }

    [Fact]
    public void ReadAll_skips_malformed_lines()
    {
        var sessionId = Guid.NewGuid().ToString();
        var path = Path.Combine(_tempDir, $"{sessionId}.jsonl");
        File.WriteAllText(path,
            "{\"type\":\"user\",\"content\":\"hi\"}\n" +
            "{not valid json\n" +
            "{\"type\":\"assistant\",\"content\":\"hello back\"}\n");

        using var store = SessionStore.OpenForResume(_tempDir, sessionId);
        var events = store.ReadAll().ToList();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<UserEvent>();
        events[1].Should().BeOfType<AssistantEvent>().Which.Content.Should().Be("hello back");
    }

    [Fact]
    public void OpenForResume_throws_when_session_file_missing()
    {
        var act = () => SessionStore.OpenForResume(_tempDir, Guid.NewGuid().ToString());
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Append_after_dispose_throws()
    {
        var store = SessionStore.Create(_tempDir);
        store.Append(new UserEvent("x"));
        store.Dispose();

        var act = () => store.Append(new UserEvent("y"));
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Append_then_resume_in_a_new_process_recovers_history()
    {
        var sessionId = Guid.NewGuid().ToString();

        // First "process" writes some events.
        using (var w = SessionStore.Create(_tempDir, sessionId))
        {
            w.Append(new MetaEvent(sessionId, "m"));
            w.Append(new UserEvent("first turn"));
            w.Append(new AssistantEvent("ok"));
        }

        // Second "process" resumes and continues.
        using (var w = SessionStore.OpenForResume(_tempDir, sessionId))
        {
            w.Append(new UserEvent("second turn"));
            w.Append(new AssistantEvent("acknowledged"));
        }

        // Third reads everything back.
        using var r = SessionStore.OpenForResume(_tempDir, sessionId);
        var events = r.ReadAll().ToList();
        events.OfType<UserEvent>().Select(e => e.Content)
            .Should().Equal("first turn", "second turn");
        events.OfType<AssistantEvent>().Select(e => e.Content)
            .Should().Equal("ok", "acknowledged");
    }
}
