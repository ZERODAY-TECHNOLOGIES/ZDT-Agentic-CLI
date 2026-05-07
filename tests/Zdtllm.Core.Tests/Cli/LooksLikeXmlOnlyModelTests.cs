using System.Collections.Immutable;
using Zdtllm.Cli;
using Zdtllm.Config;
using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Cli;

public sealed class LooksLikeXmlOnlyModelTests
{
    private static EffectiveSettings BuildSettings(
        string? defaultModel = null,
        string? toolCallingMode = null,
        IReadOnlyDictionary<string, string>? models = null)
    {
        var litellm = LiteLLMSettings.Empty with
        {
            BaseUrl = "http://x:4000",
            ApiKey = "k",
            ToolCallingMode = toolCallingMode,
            Models = models is null
                ? ImmutableDictionary<string, string>.Empty
                : ImmutableDictionary.CreateRange(StringComparer.Ordinal, models),
        };
        return EffectiveSettings.Empty with { Model = defaultModel, LiteLLM = litellm };
    }

    [Fact]
    public void ResolveModelAndMode_auto_selects_xml_for_qwen_when_no_explicit_mode()
    {
        var parsed = new ParsedArgs { Model = "medium" };
        var settings = BuildSettings(
            defaultModel: "medium",
            models: new Dictionary<string, string> { ["medium"] = "qwen-local" });

        var (model, mode) = Program.ResolveModelAndMode(parsed, settings);

        model.Should().Be("qwen-local");
        mode.Should().Be(ToolCallingMode.Xml);
    }

    [Fact]
    public void ResolveModelAndMode_honours_explicit_native_for_qwen_model()
    {
        // Critical: auto-XML must NOT fire when the user has explicitly chosen a mode. A user
        // overriding to native via CLI flag means they want native — even on a qwen model that
        // would normally trigger the heuristic.
        var parsed = new ParsedArgs { Model = "medium", ToolCallingMode = "native" };
        var settings = BuildSettings(
            defaultModel: "medium",
            models: new Dictionary<string, string> { ["medium"] = "qwen-local" });

        var (_, mode) = Program.ResolveModelAndMode(parsed, settings);

        mode.Should().Be(ToolCallingMode.Native);
    }

    [Fact]
    public void ResolveModelAndMode_honours_settings_toolCallingMode_for_qwen_model()
    {
        // Same rule but the explicit choice comes from settings.json instead of the CLI flag.
        var parsed = new ParsedArgs { Model = "medium" };
        var settings = BuildSettings(
            defaultModel: "medium",
            toolCallingMode: "native",
            models: new Dictionary<string, string> { ["medium"] = "qwen-local" });

        var (_, mode) = Program.ResolveModelAndMode(parsed, settings);

        mode.Should().Be(ToolCallingMode.Native);
    }

    [Fact]
    public void ResolveModelAndMode_falls_back_to_native_for_unknown_model_when_no_explicit_mode()
    {
        // Conservative default — only the well-known XML-only families auto-switch.
        var parsed = new ParsedArgs { Model = "medium" };
        var settings = BuildSettings(
            defaultModel: "medium",
            models: new Dictionary<string, string> { ["medium"] = "gpt-4o" });

        var (_, mode) = Program.ResolveModelAndMode(parsed, settings);

        mode.Should().Be(ToolCallingMode.Native);
    }

    [Theory]
    [InlineData("qwen-local")]
    [InlineData("Qwen/Qwen3-Coder-30B-A3B-Instruct")]
    [InlineData("glm-5.1:cloud")]
    [InlineData("glm-5:cloud")]
    [InlineData("deepseek-v3")]
    [InlineData("deepseek/deepseek-r1")]
    [InlineData("hermes-3-llama")]
    [InlineData("kimi-k2")]
    [InlineData("yi-large")]
    [InlineData("mistral-nemo-12b")]
    public void Returns_true_for_known_xml_only_model_families(string modelName)
    {
        Program.LooksLikeXmlOnlyModel(modelName).Should().BeTrue();
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("openai/o3-mini")]
    [InlineData("gemini-2.5-flash")]
    public void Returns_false_for_native_tool_calling_model_families(string modelName)
    {
        Program.LooksLikeXmlOnlyModel(modelName).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_false_for_empty_or_null(string? modelName)
    {
        Program.LooksLikeXmlOnlyModel(modelName!).Should().BeFalse();
    }
}
