using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

/// <summary>
/// The optional request-shaping passthroughs (reasoning_effort / temperature / top_p / max_tokens
/// and the verbatim extraParams escape hatch) must serialize when set and be ENTIRELY ABSENT when
/// unset — an unconfigured client is byte-for-byte identical to before, and extra params can never
/// clobber the load-bearing request keys. GLM-5.2 tuning is delivered here by config, so other
/// models routed through the same client are unaffected.
/// </summary>
public sealed class LiteLLMClientPassthroughTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static LiteLLMClient Build(StubHandler handler, LiteLLMClientOptions? opts = null) =>
        new(new HttpClient(handler), opts ?? new LiteLLMClientOptions
        {
            BaseUrl = "http://localhost:4000", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

    private static async Task<string> BodyOf(LiteLLMClient client, StubHandler handler)
    {
        var list = new List<ChatChunk>();
        await foreach (var c in client.StreamChatAsync([ChatMessage.User("hi")], tools: null, model: "glm-5.2:cloud"))
            list.Add(c);
        return handler.RequestBodies.Single();
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Unset_passthroughs_are_absent_from_the_wire_body()
    {
        var handler = new StubHandler(Sse("data: [DONE]\n\n"));
        var body = await BodyOf(Build(handler), handler);

        body.Should().NotContain("reasoning_effort");
        body.Should().NotContain("temperature");
        body.Should().NotContain("top_p");
        body.Should().NotContain("max_tokens");
        // Load-bearing shape unchanged.
        body.Should().Contain("\"stream\":true");
        body.Should().Contain("\"drop_params\":false");
    }

    [Fact]
    public async Task Set_named_passthroughs_serialize_snake_cased()
    {
        var handler = new StubHandler(Sse("data: [DONE]\n\n"));
        var opts = new LiteLLMClientOptions
        {
            BaseUrl = "http://localhost:4000", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            ReasoningEffort = "high", Temperature = 0.7, TopP = 0.95, MaxTokens = 4096,
        };
        var body = await BodyOf(Build(handler, opts), handler);

        body.Should().Contain("\"reasoning_effort\":\"high\"");
        body.Should().Contain("\"temperature\":0.7");
        body.Should().Contain("\"top_p\":0.95");
        body.Should().Contain("\"max_tokens\":4096");
    }

    [Fact]
    public async Task ExtraParams_are_emitted_verbatim()
    {
        var handler = new StubHandler(Sse("data: [DONE]\n\n"));
        var opts = new LiteLLMClientOptions
        {
            BaseUrl = "http://localhost:4000", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            ExtraParams = new Dictionary<string, JsonElement>
            {
                ["enable_thinking"] = El("false"),
                ["top_k"] = El("40"),
                ["chat_template_kwargs"] = El("{\"foo\":true}"),
            },
        };
        var body = await BodyOf(Build(handler, opts), handler);

        body.Should().Contain("\"enable_thinking\":false");   // verbatim key, no snake rename
        body.Should().Contain("\"top_k\":40");
        body.Should().Contain("\"chat_template_kwargs\":{\"foo\":true}");
    }

    [Fact]
    public async Task ExtraParams_cannot_clobber_load_bearing_or_named_keys()
    {
        var handler = new StubHandler(Sse("data: [DONE]\n\n"));
        var opts = new LiteLLMClientOptions
        {
            BaseUrl = "http://localhost:4000", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            ReasoningEffort = "high",
            ExtraParams = new Dictionary<string, JsonElement>
            {
                ["model"] = El("\"attacker-model\""),
                ["stream"] = El("false"),
                ["reasoning_effort"] = El("\"max\""), // named value must win
            },
        };
        var body = await BodyOf(Build(handler, opts), handler);

        body.Should().Contain("\"model\":\"glm-5.2:cloud\"");
        body.Should().NotContain("attacker-model");
        body.Should().Contain("\"stream\":true");
        body.Should().Contain("\"reasoning_effort\":\"high\"");
        body.Should().NotContain("\"reasoning_effort\":\"max\"");
    }
}
