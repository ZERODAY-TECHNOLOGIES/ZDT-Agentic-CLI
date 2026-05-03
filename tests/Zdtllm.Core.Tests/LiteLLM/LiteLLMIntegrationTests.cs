using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

public sealed class LiteLLMIntegrationTests
{
    [Fact]
    public async Task Streams_real_response_when_LITELLM_TEST_URL_is_set()
    {
        var url = Environment.GetEnvironmentVariable("LITELLM_TEST_URL");
        if (string.IsNullOrEmpty(url))
            return; // soft-skip per spec — no LiteLLM available locally

        var apiKey = Environment.GetEnvironmentVariable("LITELLM_TEST_API_KEY") ?? "test";
        var model = Environment.GetEnvironmentVariable("LITELLM_TEST_MODEL") ?? "gpt-3.5-turbo";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = url,
            ApiKey = apiKey,
        });

        var text = new StringBuilder();
        await foreach (var chunk in client.StreamChatAsync(
            messages: [ChatMessage.User("Reply with the single word: pong.")],
            tools: null,
            model: model))
        {
            if (chunk is ChatChunk.TextDelta td) text.Append(td.Text);
        }

        text.ToString().ToLowerInvariant().Should().Contain("pong");
    }
}
