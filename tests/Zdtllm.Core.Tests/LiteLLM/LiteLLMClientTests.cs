using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

public sealed class LiteLLMClientTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static LiteLLMClient BuildClient(StubHandler handler, int maxRetries = 3) =>
        new(
            new HttpClient(handler),
            new LiteLLMClientOptions
            {
                BaseUrl = "http://localhost:4000",
                ApiKey = "test-key",
                MaxRetries = maxRetries,
                InitialBackoff = TimeSpan.FromMilliseconds(1),
            });

    private static async Task<List<ChatChunk>> CollectAsync(IAsyncEnumerable<ChatChunk> source)
    {
        var list = new List<ChatChunk>();
        await foreach (var c in source) list.Add(c);
        return list;
    }

    [Fact]
    public async Task Posts_to_v1_chat_completions_with_bearer_auth()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
        var handler = new StubHandler(Sse(sse));
        var client = BuildClient(handler);

        await CollectAsync(client.StreamChatAsync(
            messages: [ChatMessage.User("hi")],
            tools: null,
            model: "qwen3-coder"));

        handler.Requests.Should().ContainSingle();
        var req = handler.Requests[0];
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.AbsoluteUri.Should().Be("http://localhost:4000/v1/chat/completions");
        req.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "test-key"));
    }

    [Fact]
    public async Task Serializes_request_body_with_snake_case_and_includes_usage()
    {
        var sse = "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(sse));
        var client = BuildClient(handler);

        await CollectAsync(client.StreamChatAsync(
            messages: [ChatMessage.System("sys"), ChatMessage.User("hi")],
            tools: null,
            model: "qwen3"));

        var body = handler.RequestBodies.Single();
        body.Should().Contain("\"model\":\"qwen3\"");
        body.Should().Contain("\"stream\":true");
        body.Should().Contain("\"stream_options\":{\"include_usage\":true}");
        body.Should().Contain("\"drop_params\":false");
        body.Should().Contain("\"role\":\"system\"");
        body.Should().Contain("\"role\":\"user\"");
    }

    [Fact]
    public async Task Yields_text_deltas_from_streamed_response()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\", world\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":3}}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler(Sse(sse));
        var client = BuildClient(handler);

        var chunks = await CollectAsync(
            client.StreamChatAsync([ChatMessage.User("x")], tools: null, model: "m"));

        string.Concat(chunks.OfType<ChatChunk.TextDelta>().Select(d => d.Text))
            .Should().Be("Hello, world");
        chunks.OfType<ChatChunk.Usage>().Single().PromptTokens.Should().Be(4);
        chunks.OfType<ChatChunk.Done>().Single().FinishReason.Should().Be("stop");
    }

    [Fact]
    public async Task Retries_on_429_then_succeeds()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
        var handler = new StubHandler(
            Status(HttpStatusCode.TooManyRequests, "rate limited"),
            Sse(sse));
        var client = BuildClient(handler, maxRetries: 3);

        var chunks = await CollectAsync(
            client.StreamChatAsync([ChatMessage.User("x")], tools: null, model: "m"));

        handler.Requests.Should().HaveCount(2);
        chunks.OfType<ChatChunk.TextDelta>().Should().ContainSingle(d => d.Text == "ok");
    }

    [Fact]
    public async Task Retries_on_500_then_throws_after_exhausting_attempts()
    {
        var handler = new StubHandler(
            Status(HttpStatusCode.InternalServerError, "boom"),
            Status(HttpStatusCode.BadGateway, "boom2"),
            Status(HttpStatusCode.ServiceUnavailable, "boom3"),
            Status(HttpStatusCode.InternalServerError, "boom4"));
        var client = BuildClient(handler, maxRetries: 3);

        var act = async () => await CollectAsync(
            client.StreamChatAsync([ChatMessage.User("x")], tools: null, model: "m"));

        await act.Should().ThrowAsync<LiteLLMException>()
            .Where(e => e.Message.Contains("after 4 attempts"));
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task Does_not_retry_on_400()
    {
        var handler = new StubHandler(Status(HttpStatusCode.BadRequest, "bad model"));
        var client = BuildClient(handler, maxRetries: 3);

        var act = async () => await CollectAsync(
            client.StreamChatAsync([ChatMessage.User("x")], tools: null, model: "m"));

        await act.Should().ThrowAsync<LiteLLMException>()
            .Where(e => e.Message.Contains("HTTP 400"));
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancellation_token_propagates()
    {
        var handler = new StubHandler(Status(HttpStatusCode.InternalServerError, "x"));
        var client = BuildClient(handler, maxRetries: 5);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CollectAsync(
            client.StreamChatAsync(
                [ChatMessage.User("x")], tools: null, model: "m", ct: cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

internal sealed class StubHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        Requests.Add(request);
        if (request.Content is not null)
            RequestBodies.Add(await request.Content.ReadAsStringAsync(ct));
        if (_responses.Count == 0)
            throw new InvalidOperationException("StubHandler ran out of responses.");
        return _responses.Dequeue();
    }
}
