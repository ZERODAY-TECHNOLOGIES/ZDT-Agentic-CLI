using System.Net;
using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

/// <summary>
/// Verifies how a message's <c>content</c> is serialized: a plain JSON string for text-only turns
/// (every model understands it), and the OpenAI multimodal parts array when a user turn carries
/// image attachments (vision models).
/// </summary>
public sealed class LiteLLMMultimodalTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private const string DoneSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private static LiteLLMClient BuildClient(StubHandler handler) =>
        new(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

    private static async Task DrainAsync(LiteLLMClient client, ChatMessage[] messages)
    {
        await foreach (var _ in client.StreamChatAsync(messages, tools: null, "m", CancellationToken.None)) { }
    }

    [Fact]
    public async Task Image_message_serializes_as_multimodal_content_parts()
    {
        var handler = new StubHandler(Sse(DoneSse));
        var client = BuildClient(handler);

        await DrainAsync(client, new[]
        {
            ChatMessage.UserWithImages("what is in this picture?", new[] { "data:image/png;base64,AAAA" }),
        });

        var body = handler.RequestBodies[0];
        body.Should().Contain("\"type\":\"text\"");
        body.Should().Contain("what is in this picture?");
        body.Should().Contain("\"type\":\"image_url\"");
        body.Should().Contain("\"image_url\":{\"url\":\"data:image/png;base64,AAAA\"}");
    }

    [Fact]
    public async Task Text_only_message_keeps_content_as_a_plain_string()
    {
        var handler = new StubHandler(Sse(DoneSse));
        var client = BuildClient(handler);

        await DrainAsync(client, new[] { ChatMessage.User("just text") });

        var body = handler.RequestBodies[0];
        body.Should().Contain("\"content\":\"just text\"");
        body.Should().NotContain("image_url");
    }

    [Fact]
    public async Task Multiple_images_become_multiple_image_url_parts()
    {
        var handler = new StubHandler(Sse(DoneSse));
        var client = BuildClient(handler);

        await DrainAsync(client, new[]
        {
            ChatMessage.UserWithImages("compare these", new[]
            {
                "data:image/png;base64,AAAA",
                "data:image/jpeg;base64,BBBB",
            }),
        });

        var body = handler.RequestBodies[0];
        System.Text.RegularExpressions.Regex.Matches(body, "\"type\":\"image_url\"").Count.Should().Be(2);
        body.Should().Contain("BBBB");
    }
}
