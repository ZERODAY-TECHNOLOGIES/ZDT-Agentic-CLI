using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class UserInputQueueTests
{
    [Fact]
    public void Enqueue_and_dequeue_is_fifo()
    {
        var q = new UserInputQueue();
        q.Enqueue("one");
        q.Enqueue("two");

        q.Count.Should().Be(2);
        q.HasPending.Should().BeTrue();

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be("one");
        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be("two");
        q.TryDequeue(out _).Should().BeFalse();
        q.HasPending.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void Blank_input_is_ignored(string blank)
    {
        var q = new UserInputQueue();
        q.Enqueue(blank);
        q.HasPending.Should().BeFalse();
    }

    [Fact]
    public void Enqueue_trims_surrounding_whitespace()
    {
        var q = new UserInputQueue();
        q.Enqueue("  hello  ");
        q.TryDequeue(out var m);
        m.Should().Be("hello");
    }
}
