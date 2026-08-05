using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Qwen3-family tuning: the id heuristic behind the auto-applied sampling profile (temperature 0.6 /
/// top_p 0.95 / top_k 20 / min_p 0). It exists because llama.cpp does NOT read the model's HF
/// generation_config.json and its built-in sampler defaults (temp 0.8 / top_k 40 / min_p 0.05) are
/// wrong for Qwen3, causing repetition on the A3B MoE models. Serialization of the new top_k / min_p
/// passthroughs is covered in <c>LiteLLMClientPassthroughTests</c>.
/// </summary>
public sealed class Qwen3TuningTests
{
    [Theory]
    [InlineData("Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive", true)] // the target model
    [InlineData("Qwen/Qwen3-30B-A3B-Instruct", true)]
    [InlineData("qwen3-coder", true)]
    [InlineData("QWEN3.5-14B", true)]
    [InlineData("qwen2.5-coder", false)] // Qwen2.x has a different profile — must NOT match
    [InlineData("glm-5.2:cloud", false)]
    [InlineData("llama-3.1-70b", false)]
    [InlineData(null, false)]
    public void LooksLikeQwen3_matches_qwen3_ids(string? model, bool expected)
    {
        ModelHeuristics.LooksLikeQwen3(model).Should().Be(expected);
    }
}
