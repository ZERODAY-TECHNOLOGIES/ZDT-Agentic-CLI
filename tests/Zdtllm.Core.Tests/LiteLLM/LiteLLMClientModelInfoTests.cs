using System.Net;
using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

public sealed class LiteLLMClientModelInfoTests
{
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LiteLLMClient BuildClient(StubHandler handler) =>
        new(new HttpClient(handler), new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

    [Fact]
    public async Task Parses_max_input_tokens_when_populated()
    {
        var body = """
            {"data":[
              {"model_name":"qwen-local","model_info":{"max_input_tokens":131072,"max_output_tokens":8192,"max_tokens":131072}},
              {"model_name":"gpt-4o","model_info":{"max_input_tokens":128000,"max_output_tokens":16384,"max_tokens":128000}}
            ]}
            """;
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, body)));

        var infos = await client.GetModelInfoAsync();

        infos.Should().HaveCount(2);
        infos[0].ModelName.Should().Be("qwen-local");
        infos[0].MaxInputTokens.Should().Be(131072);
        infos[0].MaxOutputTokens.Should().Be(8192);
        infos[0].EffectiveContextWindow.Should().Be(131072);
        infos[1].ModelName.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task Parses_supports_vision_flag()
    {
        var body = """
            {"data":[
              {"model_name":"gpt-4o","model_info":{"max_tokens":128000,"supports_vision":true}},
              {"model_name":"qwen-coder","model_info":{"max_tokens":32000,"supports_vision":false}},
              {"model_name":"mystery","model_info":{"max_tokens":8000}}
            ]}
            """;
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, body)));

        var infos = await client.GetModelInfoAsync();

        infos.Single(m => m.ModelName == "gpt-4o").SupportsVision.Should().BeTrue();
        infos.Single(m => m.ModelName == "qwen-coder").SupportsVision.Should().BeFalse();
        infos.Single(m => m.ModelName == "mystery").SupportsVision.Should().BeNull();
    }

    [Fact]
    public async Task Effective_window_falls_back_to_max_tokens_when_input_tokens_missing()
    {
        var body = """
            {"data":[
              {"model_name":"x","model_info":{"max_input_tokens":null,"max_tokens":40000}}
            ]}
            """;
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, body)));

        var info = (await client.GetModelInfoAsync()).Single();

        info.MaxInputTokens.Should().BeNull();
        info.MaxTokens.Should().Be(40000);
        info.EffectiveContextWindow.Should().Be(40000);
    }

    [Fact]
    public async Task Effective_window_is_null_when_proxy_lacks_metadata()
    {
        // The user's qwen-local proxy returns nulls for every token-limit field.
        var body = """
            {"data":[
              {"model_name":"qwen-local","model_info":{"max_input_tokens":null,"max_output_tokens":null,"max_tokens":null}}
            ]}
            """;
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, body)));

        var info = (await client.GetModelInfoAsync()).Single();

        info.EffectiveContextWindow.Should().BeNull();
    }

    [Fact]
    public async Task Returns_empty_on_non_200_response()
    {
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.Unauthorized, "{}")));

        var infos = await client.GetModelInfoAsync();

        infos.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_on_malformed_json()
    {
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, "not json")));

        var infos = await client.GetModelInfoAsync();

        infos.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_data_field_missing()
    {
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, "{\"meta\":{}}")));

        var infos = await client.GetModelInfoAsync();

        infos.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_entries_without_model_name()
    {
        var body = """
            {"data":[
              {"model_info":{"max_input_tokens":1000}},
              {"model_name":"good","model_info":{"max_input_tokens":2000}}
            ]}
            """;
        var client = BuildClient(new StubHandler(Json(HttpStatusCode.OK, body)));

        var infos = await client.GetModelInfoAsync();

        infos.Should().ContainSingle();
        infos[0].ModelName.Should().Be("good");
    }
}
