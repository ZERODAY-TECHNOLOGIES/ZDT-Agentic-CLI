using System.Net;
using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

/// <summary>
/// The streaming idle watchdog: because the HTTP timeout is intentionally infinite, a wedged backend
/// that accepts the socket but never sends bytes must be aborted by a per-chunk idle deadline —
/// otherwise the CLI hangs forever ("completely frozen"). The clock resets on every chunk, so a
/// normal stream is unaffected, and it can be disabled with InfiniteTimeSpan.
/// </summary>
public sealed class LiteLLMClientIdleTimeoutTests
{
    private static LiteLLMClient Build(HttpMessageHandler handler, TimeSpan idle) =>
        new(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://x:4000", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            StreamIdleTimeout = idle,
        });

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task Idle_timeout_aborts_a_stalled_stream_with_TimeoutException()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() });
        var client = Build(handler, TimeSpan.FromMilliseconds(150));

        var act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync([ChatMessage.User("hi")], tools: null, "qwen36")) { }
        };

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Normal_stream_completes_with_the_watchdog_enabled()
    {
        var body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var client = Build(new StubHandler(Sse(body)), TimeSpan.FromSeconds(30));

        var chunks = new List<ChatChunk>();
        await foreach (var c in client.StreamChatAsync([ChatMessage.User("hi")], tools: null, "qwen36"))
            chunks.Add(c);

        chunks.OfType<ChatChunk.TextDelta>().Select(t => t.Text).Should().Contain("hi");
    }

    [Fact]
    public async Task Disabled_watchdog_leaves_a_normal_stream_untouched()
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
        var client = Build(new StubHandler(Sse(body)), Timeout.InfiniteTimeSpan);

        var chunks = new List<ChatChunk>();
        await foreach (var c in client.StreamChatAsync([ChatMessage.User("hi")], tools: null, "qwen36"))
            chunks.Add(c);

        chunks.Should().NotBeEmpty();
    }

    /// <summary>A content whose read stream accepts the read but never produces bytes — a wedged backend.</summary>
    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StallingStream());
    }

    private sealed class StallingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // block until the (linked) token fires
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
