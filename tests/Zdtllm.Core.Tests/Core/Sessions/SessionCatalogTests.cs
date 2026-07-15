using Zdtllm.Core.Sessions;

namespace Zdtllm.Core.Tests.Core.Sessions;

public sealed class SessionCatalogTests : IDisposable
{
    private readonly string _tempDir;

    public SessionCatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private string WriteSession(string id, string model, params SessionEvent[] events)
    {
        using var store = SessionStore.Create(_tempDir, id);
        store.Append(new MetaEvent(id, model));
        foreach (var ev in events) store.Append(ev);
        return id;
    }

    [Fact]
    public void List_returns_empty_for_missing_directory()
    {
        var catalog = new SessionCatalog(Path.Combine(_tempDir, "does-not-exist"));
        catalog.List().Should().BeEmpty();
    }

    [Fact]
    public void List_summarizes_first_user_message_as_title_and_counts_assistant_turns()
    {
        WriteSession("s1", "heavy",
            new UserEvent("Fix the login bug"),
            new AssistantEvent("Looking into it"),
            new UserEvent("also check logout"),
            new AssistantEvent("done"));

        var summary = new SessionCatalog(_tempDir).List().Single();

        summary.Id.Should().Be("s1");
        summary.Model.Should().Be("heavy");
        summary.Title.Should().Be("Fix the login bug");
        summary.AssistantTurns.Should().Be(2);
        summary.UserTurns.Should().Be(2);
    }

    [Fact]
    public void List_orders_newest_first_by_modification_time()
    {
        WriteSession("old", "m", new UserEvent("old one"));
        // Nudge mtimes so ordering is deterministic regardless of write speed.
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, "old.jsonl"), DateTime.UtcNow.AddHours(-2));
        WriteSession("new", "m", new UserEvent("new one"));
        File.SetLastWriteTimeUtc(Path.Combine(_tempDir, "new.jsonl"), DateTime.UtcNow);

        var ids = new SessionCatalog(_tempDir).List().Select(s => s.Id).ToList();

        ids.Should().Equal("new", "old");
    }

    [Fact]
    public void List_respects_limit()
    {
        for (var i = 0; i < 5; i++)
            WriteSession($"s{i}", "m", new UserEvent($"msg {i}"));

        new SessionCatalog(_tempDir).List(limit: 2).Should().HaveCount(2);
    }

    [Fact]
    public void List_skips_files_with_no_meta_event()
    {
        // A file that isn't a real session (no meta) must be ignored, not throw.
        File.WriteAllText(Path.Combine(_tempDir, "garbage.jsonl"), "not json at all\n{\"type\":\"user\"}\n");
        WriteSession("good", "m", new UserEvent("hello"));

        var ids = new SessionCatalog(_tempDir).List().Select(s => s.Id).ToList();

        ids.Should().Equal("good");
    }

    [Fact]
    public void Title_uses_first_nonblank_line_of_a_multiline_prompt()
    {
        WriteSession("s", "m", new UserEvent("\n\n  Do the thing  \nsecond line"));

        new SessionCatalog(_tempDir).List().Single().Title.Should().Be("Do the thing");
    }
}
