using Zdtllm.Core.Sessions;

namespace Zdtllm.Core.Tests.Core.Sessions;

public sealed class RecentTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _recentPath;

    public RecentTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _recentPath = Path.Combine(_tempDir, "recent.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void GetMostRecentForCwd_returns_null_when_unknown()
    {
        var tracker = new RecentTracker(_recentPath);

        tracker.GetMostRecentForCwd(_tempDir).Should().BeNull();
    }

    [Fact]
    public void Mark_then_Get_returns_the_session_id()
    {
        var tracker = new RecentTracker(_recentPath);

        tracker.Mark(_tempDir, "session-1");

        tracker.GetMostRecentForCwd(_tempDir).Should().Be("session-1");
    }

    [Fact]
    public void Most_recent_overwrites_per_cwd()
    {
        var tracker = new RecentTracker(_recentPath);

        tracker.Mark(_tempDir, "session-1");
        tracker.Mark(_tempDir, "session-2");

        tracker.GetMostRecentForCwd(_tempDir).Should().Be("session-2");
    }

    [Fact]
    public void Different_cwds_are_tracked_independently()
    {
        var cwdA = Path.Combine(_tempDir, "a");
        var cwdB = Path.Combine(_tempDir, "b");
        Directory.CreateDirectory(cwdA);
        Directory.CreateDirectory(cwdB);

        var tracker = new RecentTracker(_recentPath);
        tracker.Mark(cwdA, "session-A");
        tracker.Mark(cwdB, "session-B");

        tracker.GetMostRecentForCwd(cwdA).Should().Be("session-A");
        tracker.GetMostRecentForCwd(cwdB).Should().Be("session-B");
    }

    [Fact]
    public void Persists_across_instances()
    {
        var first = new RecentTracker(_recentPath);
        first.Mark(_tempDir, "session-X");

        var second = new RecentTracker(_recentPath);
        second.GetMostRecentForCwd(_tempDir).Should().Be("session-X");
    }

    [Fact]
    public void Corrupted_file_is_replaced_rather_than_throwing()
    {
        File.WriteAllText(_recentPath, "{not valid json");

        var tracker = new RecentTracker(_recentPath);

        // Doesn't throw; treats as empty.
        tracker.GetMostRecentForCwd(_tempDir).Should().BeNull();
        tracker.Mark(_tempDir, "session-recovered");
        tracker.GetMostRecentForCwd(_tempDir).Should().Be("session-recovered");
    }

    [Fact]
    public void On_case_insensitive_filesystems_path_case_does_not_matter()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;

        var tracker = new RecentTracker(_recentPath);
        tracker.Mark(_tempDir, "session-X");

        var upcased = _tempDir.ToUpperInvariant();
        tracker.GetMostRecentForCwd(upcased).Should().Be("session-X");
    }

    [Fact]
    public void Trailing_separator_is_normalized_away()
    {
        var tracker = new RecentTracker(_recentPath);
        tracker.Mark(_tempDir, "session-X");

        tracker.GetMostRecentForCwd(_tempDir + Path.DirectorySeparatorChar)
            .Should().Be("session-X");
    }
}
