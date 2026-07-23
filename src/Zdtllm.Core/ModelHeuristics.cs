namespace Zdtllm.Core;

/// <summary>
/// Single source of truth for the "does this model id look like an open-weights model that lacks
/// reliable OpenAI-shaped native function-calling?" heuristic. Used to auto-select
/// <see cref="ToolCallingMode.Xml"/> when the user hasn't pinned a mode explicitly.
///
/// <para>
/// Called from BOTH the runtime resolver (<c>Program.ResolveModelAndMode</c>) and the setup wizard
/// (<c>SetupWizard.SuggestMode</c>) so the two can never drift — previously they disagreed (the
/// wizard suggested native for GLM while the runtime forced XML), which made the effective transport
/// depend on the install path.
/// </para>
///
/// <para>
/// GLM is deliberately NOT a marker: GLM-4.5/4.6/5.2 expose reliable native tool-calling through an
/// OpenAI-compatible endpoint (vLLM's <c>--tool-call-parser glm47/glm45</c> + <c>--enable-auto-tool-choice</c>
/// translate GLM's XML chat template into standard JSON <c>tool_calls</c> server-side), so the correct
/// client transport there is native. A GLM endpoint that is a raw passthrough with no server-side tool
/// parser must set <c>toolCallingMode=xml</c> explicitly — the same opt-in every non-marker model needs.
/// </para>
///
/// Matched as case-insensitive substrings so versioned ids
/// (<c>Qwen/Qwen3-Coder-30B-A3B-Instruct</c>, <c>mistral-nemo-12b</c>, <c>my-local-llama</c>) still trigger.
/// Wrong matches only push a dual-mode model onto XML (slightly more verbose tool calls, no functional
/// regression); missed matches leave the conservative "explicit-or-native" default.
/// </summary>
public static class ModelHeuristics
{
    // Union of the two historical marker sets, minus "glm". "local" covers self-hosted / template-only
    // deployments in both call paths (a deliberate, low-risk widening of the runtime path).
    private static readonly string[] XmlOnlyMarkers =
    [
        "qwen", "deepseek", "hermes", "kimi", "yi-", "nemo", "local",
    ];

    public static bool LooksLikeXmlOnly(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        foreach (var m in XmlOnlyMarkers)
            if (modelName.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// True when the model id looks like a GLM (Zhipu/Z.ai) model — GLM-4.5/4.6/5.2 and friends.
    /// GLM is reasoning-first: with <c>reasoning_effort</c> unset the server thinks at its default
    /// <c>max</c> tier on EVERY turn (including trivial tool-continuation turns), which is slow and
    /// costly. zdt uses this to default <c>reasoning_effort</c> to <c>"high"</c> for a GLM model when
    /// the user hasn't pinned it. Matched as a case-insensitive substring so versioned ids
    /// (<c>glm-5.2:cloud</c>, <c>zhipu/glm-4.6</c>) still trigger.
    /// </summary>
    public static bool LooksLikeGlm(string? modelName) =>
        !string.IsNullOrEmpty(modelName) && modelName.Contains("glm", StringComparison.OrdinalIgnoreCase);
}
