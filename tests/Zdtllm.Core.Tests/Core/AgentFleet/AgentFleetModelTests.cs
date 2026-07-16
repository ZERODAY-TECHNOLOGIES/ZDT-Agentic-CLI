using Zdtllm.Core.AgentFleet;

namespace Zdtllm.Core.Tests.Core.AgentFleet;

public sealed class AgentFleetModelTests
{
    [Fact]
    public void Register_append_and_complete_track_state()
    {
        var m = new AgentFleetModel();
        var a = m.Register("Draft: moon");
        var b = m.Register("Draft: ocean");

        m.Count.Should().Be(2);
        m.ActiveCount.Should().Be(2);

        m.Append(a, "line 1");
        m.Append(a, "line 2");
        m.Complete(b, failed: false);

        m.ActiveCount.Should().Be(1);
        var snap = m.Snapshot();
        snap.Single(s => s.Id == a).Status.Should().Be(AgentRunStatus.Running);
        snap.Single(s => s.Id == a).RecentLines.Should().Equal("line 1", "line 2");
        snap.Single(s => s.Id == b).Status.Should().Be(AgentRunStatus.Done);
    }

    [Fact]
    public void Complete_with_failed_marks_failed()
    {
        var m = new AgentFleetModel();
        var a = m.Register("x");
        m.Complete(a, failed: true);
        m.Snapshot()[0].Status.Should().Be(AgentRunStatus.Failed);
    }

    [Fact]
    public void Focus_next_prev_wrap_around()
    {
        var m = new AgentFleetModel();
        m.Register("a"); m.Register("b"); m.Register("c");

        m.FocusIndex.Should().Be(0);
        m.FocusNext(); m.FocusIndex.Should().Be(1);
        m.FocusNext(); m.FocusIndex.Should().Be(2);
        m.FocusNext(); m.FocusIndex.Should().Be(0); // wraps
        m.FocusPrev(); m.FocusIndex.Should().Be(2); // wraps back
    }

    [Fact]
    public void Focus_by_index_jumps_and_ignores_out_of_range()
    {
        var m = new AgentFleetModel();
        m.Register("a"); m.Register("b"); m.Register("c");

        m.Focus(2); m.FocusIndex.Should().Be(2);
        m.Focus(9); m.FocusIndex.Should().Be(2); // ignored
        m.Focus(-1); m.FocusIndex.Should().Be(2); // ignored
    }

    [Fact]
    public void Focused_snapshot_marks_the_focused_agent()
    {
        var m = new AgentFleetModel();
        m.Register("a"); m.Register("b");
        m.FocusNext(); // focus b (index 1)

        m.Focused(recentLines: 5)!.Label.Should().Be("b");
        m.Snapshot().Single(s => s.Focused).Label.Should().Be("b");
    }

    [Fact]
    public void Recent_lines_are_capped_and_return_the_tail()
    {
        var m = new AgentFleetModel { MaxLinesPerAgent = 10 };
        var a = m.Register("x");
        for (var i = 0; i < 25; i++) m.Append(a, $"L{i}");

        var all = m.Snapshot()[0].RecentLines;
        all.Should().HaveCount(10);
        all[^1].Should().Be("L24"); // newest kept

        var tail = m.Focused(recentLines: 3)!.RecentLines;
        tail.Should().Equal("L22", "L23", "L24");
    }

    [Fact]
    public void Empty_model_has_no_focused_agent()
    {
        new AgentFleetModel().Focused(5).Should().BeNull();
    }

    [Fact]
    public void Concurrent_appends_are_safe()
    {
        var m = new AgentFleetModel { MaxLinesPerAgent = 100000 };
        var ids = Enumerable.Range(0, 4).Select(i => m.Register($"a{i}")).ToArray();

        Parallel.For(0, 4000, i => m.Append(ids[i % 4], $"line{i}"));

        m.Snapshot().Sum(s => s.RecentLines.Count).Should().Be(4000);
    }
}
