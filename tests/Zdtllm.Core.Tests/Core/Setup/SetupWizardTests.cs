using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Zdtllm.Core.Setup;
using Zdtllm.Core.Tests.LiteLLM;

namespace Zdtllm.Core.Tests.Core.Setup;

public sealed class SetupWizardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _targetPath;

    public SetupWizardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _targetPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static HttpResponseMessage ModelsResponse(params string[] modelIds)
    {
        var data = string.Join(",", modelIds.Select(id => $"{{\"id\":\"{id}\",\"object\":\"model\"}}"));
        var body = $"{{\"data\":[{data}],\"object\":\"list\"}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private (SetupWizard wizard, StringWriter output) Wizard(string scriptedInput, params HttpResponseMessage[] discoveryResponses)
    {
        var http = new HttpClient(new StubHandler(discoveryResponses));
        var input = new StringReader(scriptedInput);
        var output = new StringWriter();
        return (new SetupWizard(input, output, http), output);
    }

    private static string Lines(params string[] entries) => string.Join('\n', entries) + '\n';

    [Fact]
    public async Task Happy_path_writes_expected_settings_with_picks_from_discovered_list()
    {
        var (wizard, _) = Wizard(
            Lines(
                "http://localhost:4000",
                "sk-test",
                "1",         // light = qwen-local (first match)
                "1",         // medium = qwen-local
                "qwen-max",  // heavy = custom name
                "xml",
                "medium",
                "y"),
            ModelsResponse("qwen-local", "qwen-coder"));

        var result = await wizard.RunAsync(_targetPath);

        result.UserConfirmed.Should().BeTrue();
        File.Exists(_targetPath).Should().BeTrue();

        var node = JsonNode.Parse(await File.ReadAllTextAsync(_targetPath))!;
        node["model"]!.GetValue<string>().Should().Be("medium");
        var litellm = node["litellm"]!;
        litellm["baseUrl"]!.GetValue<string>().Should().Be("http://localhost:4000");
        litellm["apiKey"]!.GetValue<string>().Should().Be("sk-test");
        litellm["toolCallingMode"]!.GetValue<string>().Should().Be("xml");
        litellm["models"]!["light"]!.GetValue<string>().Should().Be("qwen-local");
        litellm["models"]!["medium"]!.GetValue<string>().Should().Be("qwen-local");
        litellm["models"]!["heavy"]!.GetValue<string>().Should().Be("qwen-max");
    }

    [Fact]
    public async Task User_aborts_at_confirm_does_not_write_file()
    {
        var (wizard, _) = Wizard(
            Lines(
                "http://localhost:4000",
                "",            // no api key
                "x", "x", "x", // model picks (free-form)
                "native",
                "medium",
                "n"),          // abort
            ModelsResponse("anything"));

        var result = await wizard.RunAsync(_targetPath);

        result.UserConfirmed.Should().BeFalse();
        File.Exists(_targetPath).Should().BeFalse();
    }

    [Fact]
    public async Task Discovery_failure_does_not_block_the_wizard()
    {
        // No queued response → StubHandler throws → DiscoverModelsAsync swallows and returns null.
        var (wizard, output) = Wizard(
            Lines(
                "http://offline:4000",
                "key",
                "my-light",
                "my-medium",
                "my-heavy",
                "xml",
                "medium",
                "y"));

        var result = await wizard.RunAsync(_targetPath);

        result.UserConfirmed.Should().BeTrue();
        var node = JsonNode.Parse(await File.ReadAllTextAsync(_targetPath))!;
        node["litellm"]!["models"]!["light"]!.GetValue<string>().Should().Be("my-light");
        output.ToString().Should().Contain("could not connect");
    }

    [Fact]
    public async Task Existing_settings_file_is_merged_preserving_other_keys()
    {
        await File.WriteAllTextAsync(_targetPath, """
            {
              "permissions": {
                "allow": ["Read", "Bash(git status *)"]
              },
              "env": { "FOO": "bar" }
            }
            """);

        var (wizard, _) = Wizard(
            Lines(
                "http://localhost:4000",
                "",
                "1", "1", "1",
                "xml",
                "medium",
                "y"),
            ModelsResponse("qwen-local"));

        await wizard.RunAsync(_targetPath);

        var node = JsonNode.Parse(await File.ReadAllTextAsync(_targetPath))!;

        node["permissions"]!["allow"]!.AsArray().Should().HaveCount(2);
        node["env"]!["FOO"]!.GetValue<string>().Should().Be("bar");
        node["litellm"]!["baseUrl"]!.GetValue<string>().Should().Be("http://localhost:4000");
        node["litellm"]!["models"]!["light"]!.GetValue<string>().Should().Be("qwen-local");
    }

    [Fact]
    public async Task Empty_api_key_omits_the_field_from_output()
    {
        var (wizard, _) = Wizard(
            Lines(
                "http://localhost:4000",
                "",            // no api key
                "1", "1", "1",
                "xml",
                "medium",
                "y"),
            ModelsResponse("local-model"));

        await wizard.RunAsync(_targetPath);

        var json = await File.ReadAllTextAsync(_targetPath);
        json.Should().NotContain("apiKey");
    }

    [Fact]
    public async Task Env_var_placeholder_in_api_key_is_kept_verbatim_for_runtime_expansion()
    {
        var (wizard, _) = Wizard(
            Lines(
                "http://localhost:4000",
                "${ZDTLLM_API_KEY}",
                "1", "1", "1",
                "native",
                "medium",
                "y"),
            ModelsResponse("foo"));

        await wizard.RunAsync(_targetPath);

        var node = JsonNode.Parse(await File.ReadAllTextAsync(_targetPath))!;
        node["litellm"]!["apiKey"]!.GetValue<string>().Should().Be("${ZDTLLM_API_KEY}");
    }

    [Theory]
    [InlineData("qwen-local", "xml")]           // matches "local" (NOT "qwen") — self-hosted id stays XML
    [InlineData("deepseek-r1", "xml")]
    [InlineData("ollama-local-llama", "xml")]
    [InlineData("hermes-3", "xml")]      // now unified with the runtime marker set
    [InlineData("kimi-k2", "xml")]
    [InlineData("gpt-4o", "native")]
    [InlineData("claude-sonnet-4", "native")]
    [InlineData("glm-5.2:cloud", "native")]   // GLM defaults to native (OpenAI-compatible endpoint)
    [InlineData("Qwen/Qwen3-Coder-30B", "native")] // Qwen now defaults to native (live-verified on llama.cpp --jinja)
    [InlineData("qwen36", "native")]
    public void Mode_suggestion_picks_xml_for_local_open_weights_else_native(string model, string expected)
    {
        SetupWizard.SuggestMode(model).Should().Be(expected);
    }

    [Fact]
    public async Task Invalid_url_re_prompts_until_a_valid_one_is_supplied()
    {
        var (wizard, _) = Wizard(
            Lines(
                "not-a-url",
                "http://localhost:4000",
                "",
                "x", "x", "x",
                "native",
                "medium",
                "y"),
            ModelsResponse("anything"));

        await wizard.RunAsync(_targetPath);

        var node = JsonNode.Parse(await File.ReadAllTextAsync(_targetPath))!;
        node["litellm"]!["baseUrl"]!.GetValue<string>().Should().Be("http://localhost:4000");
    }
}
