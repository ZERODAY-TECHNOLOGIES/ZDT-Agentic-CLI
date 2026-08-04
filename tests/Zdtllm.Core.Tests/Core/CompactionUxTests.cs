using FluentAssertions;
using Zdtllm.Core;
using Xunit;

namespace Zdtllm.Core.Tests.Core;

public class CompactionUxTests
{
    private sealed class FakeCapture : ITurnInputCapture
    {
        public int BeginCompactingCalls;
        public int Disposals;
        public bool WorkRanWhileActive;
        private bool _active;

        public void BeginCapture() { }
        public Task EndCaptureAsync() => Task.CompletedTask;

        public IDisposable BeginCompacting()
        {
            BeginCompactingCalls++;
            _active = true;
            return new Scope(this);
        }

        public void MarkWork() => WorkRanWhileActive = _active;

        private sealed class Scope : IDisposable
        {
            private readonly FakeCapture _c;
            public Scope(FakeCapture c) => _c = c;
            public void Dispose() { _c.Disposals++; _c._active = false; }
        }
    }

    [Fact]
    public async Task Uses_the_capture_indicator_when_no_rich_console()
    {
        var capture = new FakeCapture();

        var result = await CompactionUx.RunAsync(rich: null, capture: capture, compact: () =>
        {
            capture.MarkWork();
            return Task.FromResult(7);
        });

        result.Should().Be(7);
        capture.BeginCompactingCalls.Should().Be(1);
        capture.WorkRanWhileActive.Should().BeTrue("the work runs inside the indicator's lifetime");
        capture.Disposals.Should().Be(1, "the indicator is ended when compaction finishes");
    }

    [Fact]
    public async Task Falls_back_to_just_running_the_work_when_no_front_end()
    {
        var result = await CompactionUx.RunAsync(rich: null, capture: null,
            compact: () => Task.FromResult(3));

        result.Should().Be(3);
    }

    [Fact]
    public async Task Ends_the_indicator_even_if_compaction_throws()
    {
        var capture = new FakeCapture();

        var act = async () => await CompactionUx.RunAsync(rich: null, capture: capture,
            compact: () => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Disposals.Should().Be(1, "the using-scope must dispose on the exception path too");
    }
}
