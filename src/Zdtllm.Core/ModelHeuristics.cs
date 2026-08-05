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
/// <para>
/// Qwen is ALSO deliberately NOT a marker (removed 2026-08): a modern llama.cpp (<c>--jinja</c>) or vLLM
/// (<c>--tool-call-parser hermes</c>) returns clean OpenAI <c>tool_calls</c> for Qwen3 — verified LIVE
/// against a Qwen3.6-A3B route on llama.cpp b10210, including pathological argument values (a
/// <c>&lt;/tool_call&gt;</c> nested inside a parameter). And even on a raw-passthrough Qwen server,
/// native mode's salvage path (AgentLoop re-parses <c>&lt;tool_call&gt;</c> markup that leaks into
/// content) covers the no-parser case. So native is the strictly-better default; forcing XML made zdt
/// DISCARD the server's clean <c>tool_calls</c> and regex-parse text instead — which broke exactly on
/// tool-call markup nested in a parameter value. A Qwen server that genuinely needs XML sets
/// <c>toolCallingMode=xml</c> explicitly.
/// </para>
///
/// Matched as case-insensitive substrings so versioned ids
/// (<c>deepseek/deepseek-r1</c>, <c>mistral-nemo-12b</c>, <c>my-local-llama</c>) still trigger.
/// Wrong matches only push a dual-mode model onto XML (slightly more verbose tool calls, no functional
/// regression); missed matches leave the conservative "explicit-or-native" default.
/// </summary>
public static class ModelHeuristics
{
    // Historical marker set minus "glm" and "qwen" — both parse native tool_calls on a modern runtime
    // (see the type doc for the live Qwen evidence). "local" covers self-hosted / template-only
    // deployments in both call paths (a deliberate, low-risk widening of the runtime path).
    private static readonly string[] XmlOnlyMarkers =
    [
        "deepseek", "hermes", "kimi", "yi-", "nemo", "local",
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

    /// <summary>
    /// True when the model id looks like a Qwen3-family model (Qwen3, Qwen3.5/3.6, and the A3B MoE
    /// variants / community fine-tunes like <c>Qwen3.6-35B-A3B-…</c>). Matched as a case-insensitive
    /// substring on <c>"qwen3"</c> so versioned ids still trigger.
    /// <para>
    /// Used to default Qwen3's documented sampling profile (temperature 0.6 / top_p 0.95 / top_k 20 /
    /// min_p 0) when the user hasn't pinned it. This matters specifically for local llama.cpp routes:
    /// llama.cpp does NOT read the model's HF <c>generation_config.json</c>, and its built-in sampler
    /// defaults (temp 0.8 / top_k 40 / min_p 0.05 / top_p 0.9) are wrong for Qwen3 — they cause
    /// quality loss and the repetition loops these MoE models are prone to. The client must send the
    /// right values explicitly on every request. Any explicit <c>litellm.*</c> value still wins.
    /// </para>
    /// </summary>
    public static bool LooksLikeQwen3(string? modelName) =>
        !string.IsNullOrEmpty(modelName) && modelName.Contains("qwen3", StringComparison.OrdinalIgnoreCase);
}
