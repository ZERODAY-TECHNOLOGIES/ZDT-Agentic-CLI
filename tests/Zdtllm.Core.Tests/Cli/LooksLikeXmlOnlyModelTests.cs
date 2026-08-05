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
    public void ResolveModelAndMode_auto_selects_native_for_qwen_when_no_explicit_mode()
    {
        // Qwen defaults to NATIVE now (verified live: a modern llama.cpp --jinja route returns clean
        // OpenAI tool_calls for Qwen3, even for a </tool_call> nested in a parameter value). Forcing
        // XML here was the ROOT CAUSE of the tool-call parse failures — zdt discarded the server's
        // clean tool_calls and regex-parsed text instead.
        var parsed = new ParsedArgs { Model = "medium" };
        var settings = BuildSettings(
            defaultModel: "medium",
            models: new Dictionary<string, string> { ["medium"] = "qwen36" });

        var (model, mode) = Program.ResolveModelAndMode(parsed, settings);

        model.Should().Be("qwen36");
        mode.Should().Be(ToolCallingMode.Native);
    }

    [Fact]
    public void ResolveModelAndMode_honours_explicit_native_for_qwen_model()
    {
        // An explicit native flag is honored on a qwen model (which now defaults to native anyway) —
        // exercises the CLI-flag path distinctly from the default.
        var parsed = new ParsedArgs { Model = "medium", ToolCallingMode = "native" };
        var settings = BuildSettings(
            defaultModel: "medium",
            models: new Dictionary<string, string> { ["medium"] = "qwen36" });

        var (_, mode) = Program.ResolveModelAndMode(parsed, settings);

        mode.Should().Be(ToolCallingMode.Native);
    }

    [Fact]
    public void ResolveModelAndMode_honours_explicit_xml_for_qwen_model()
    {
        // The now-relevant override: a Qwen server that IS a raw passthrough (no server-side tool
        // parser) can still force XML explicitly, and that choice must win over the native default.
        var parsed = new ParsedArgs { Model = "medium" };
        var settings = BuildSettings(
            defaultModel: "medium",
            toolCallingMode: "xml",
            models: new Dictionary<string, string> { ["medium"] = "qwen36" });

        var (_, mode) = Program.ResolveModelAndMode(parsed, settings);

        mode.Should().Be(ToolCallingMode.Xml);
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

    [Fact]
    public void ResolveModelAndMode_defaults_glm_to_native_when_no_explicit_mode()
    {
        // GLM-5.2 serves native tool_calls through an OpenAI-compatible endpoint, so a fresh GLM
        // user (no toolCallingMode set) must land on native, not XML.
        var parsed = new ParsedArgs { Model = "medium" };
        var settings = BuildSettings(
            defaultModel: "medium",
            models: new Dictionary<string, string> { ["medium"] = "glm-5.2:cloud" });

        var (model, mode) = Program.ResolveModelAndMode(parsed, settings);

        model.Should().Be("glm-5.2:cloud");
        mode.Should().Be(ToolCallingMode.Native);
    }

    [Theory]
    [InlineData("deepseek-v3")]
    [InlineData("deepseek/deepseek-r1")]
    [InlineData("hermes-3-llama")]
    [InlineData("kimi-k2")]
    [InlineData("yi-large")]
    [InlineData("mistral-nemo-12b")]
    [InlineData("my-local-llama")]
    [InlineData("qwen-local")]   // matches "local" (NOT "qwen") — a self-hosted/template id stays XML-only
    public void Returns_true_for_known_xml_only_model_families(string modelName)
    {
        Program.LooksLikeXmlOnlyModel(modelName).Should().BeTrue();
        Zdtllm.Core.ModelHeuristics.LooksLikeXmlOnly(modelName).Should().BeTrue();
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("openai/o3-mini")]
    [InlineData("gemini-2.5-flash")]
    [InlineData("glm-5.2:cloud")]   // GLM is native on an OpenAI-compatible endpoint
    [InlineData("glm-5.1:cloud")]
    [InlineData("glm-5:cloud")]
    [InlineData("qwen36")]                              // Qwen parses native tool_calls on modern llama.cpp
    [InlineData("Qwen/Qwen3-Coder-30B-A3B-Instruct")]
    public void Returns_false_for_native_tool_calling_model_families(string modelName)
    {
        Program.LooksLikeXmlOnlyModel(modelName).Should().BeFalse();
        Zdtllm.Core.ModelHeuristics.LooksLikeXmlOnly(modelName).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_false_for_empty_or_null(string? modelName)
    {
        Program.LooksLikeXmlOnlyModel(modelName!).Should().BeFalse();
    }
}
