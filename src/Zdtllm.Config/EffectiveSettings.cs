using System.Collections.Immutable;
using System.Text.Json;

namespace Zdtllm.Config;

public sealed record EffectiveSettings(
    string? Model,
    PermissionsSettings Permissions,
    ImmutableDictionary<string, string> Env,
    LiteLLMSettings LiteLLM,
    McpSettings Mcp)
{
    public static EffectiveSettings Empty { get; } = new(
        Model: null,
        Permissions: PermissionsSettings.Empty,
        Env: ImmutableDictionary<string, string>.Empty,
        LiteLLM: LiteLLMSettings.Empty,
        Mcp: McpSettings.Empty);

    public EffectiveSettings Merge(EffectiveSettings higher) => new(
        Model: higher.Model ?? Model,
        Permissions: Permissions.Merge(higher.Permissions),
        Env: MergeOverride(Env, higher.Env),
        LiteLLM: LiteLLM.Merge(higher.LiteLLM),
        Mcp: Mcp.Merge(higher.Mcp));

    internal static ImmutableDictionary<string, T> MergeOverride<T>(
        ImmutableDictionary<string, T> lower,
        ImmutableDictionary<string, T> higher)
    {
        if (higher.IsEmpty) return lower;
        if (lower.IsEmpty) return higher;
        var b = lower.ToBuilder();
        foreach (var kv in higher) b[kv.Key] = kv.Value;
        return b.ToImmutable();
    }

    internal static ImmutableArray<string> ConcatDedup(
        ImmutableArray<string> lower,
        ImmutableArray<string> higher)
    {
        if (lower.IsDefaultOrEmpty) return higher.IsDefault ? ImmutableArray<string>.Empty : higher;
        if (higher.IsDefaultOrEmpty) return lower;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var b = ImmutableArray.CreateBuilder<string>(lower.Length + higher.Length);
        foreach (var s in lower) if (seen.Add(s)) b.Add(s);
        foreach (var s in higher) if (seen.Add(s)) b.Add(s);
        return b.ToImmutable();
    }
}

public sealed record PermissionsSettings(
    ImmutableArray<string> Allow,
    ImmutableArray<string> Ask,
    ImmutableArray<string> Deny,
    ImmutableArray<string> AdditionalDirectories,
    string? DefaultMode)
{
    public static PermissionsSettings Empty { get; } = new(
        Allow: ImmutableArray<string>.Empty,
        Ask: ImmutableArray<string>.Empty,
        Deny: ImmutableArray<string>.Empty,
        AdditionalDirectories: ImmutableArray<string>.Empty,
        DefaultMode: null);

    public PermissionsSettings Merge(PermissionsSettings higher) => new(
        Allow: EffectiveSettings.ConcatDedup(Allow, higher.Allow),
        Ask: EffectiveSettings.ConcatDedup(Ask, higher.Ask),
        Deny: EffectiveSettings.ConcatDedup(Deny, higher.Deny),
        AdditionalDirectories: EffectiveSettings.ConcatDedup(AdditionalDirectories, higher.AdditionalDirectories),
        DefaultMode: higher.DefaultMode ?? DefaultMode);
}

public sealed record LiteLLMSettings(
    string? BaseUrl,
    string? ApiKey,
    int? TimeoutSeconds,
    string? ToolCallingMode,
    ImmutableDictionary<string, string> Models,
    ImmutableDictionary<string, int> ContextWindows,
    ImmutableDictionary<string, string> SubagentModels,
    /// <summary>
    /// Implicit small/fast model used by read-only subagents (code-reviewer, explore) when
    /// the user hasn't pinned them via <see cref="SubagentModels"/>. Populated from the
    /// <c>ZDT_SMALL_FAST_MODEL</c> env var (the zdt rename of claude-cli's
    /// <c>ANTHROPIC_SMALL_FAST_MODEL</c>) so a single env line can route every fast
    /// subagent through one cheap model without touching settings.json.
    /// </summary>
    string? SmallFastModel,
    /// <summary>
    /// Explicit override for whether the model accepts images (vision). When set, it wins over
    /// LiteLLM <c>/model/info</c> <c>supports_vision</c> auto-detection. Null = auto-detect.
    /// </summary>
    bool? Vision = null,
    /// <summary>
    /// Reasoning-effort passthrough for reasoning models (GLM-5.2 <c>reasoning_effort</c>:
    /// <c>"high"</c>/<c>"max"</c>; OpenAI o-series: <c>"low"</c>/<c>"medium"</c>/<c>"high"</c>).
    /// Null = send nothing (the request stays byte-for-byte identical and the server applies its
    /// own default — for GLM-5.2 that is <c>max</c>). Opt-in per deployment; never defaulted, since
    /// the accepted key/values are provider-specific and <c>drop_params:false</c> forwards unknowns.
    /// </summary>
    string? ReasoningEffort = null,
    /// <summary>Sampling temperature passthrough. Null = omit (server default). NOTE for GLM-5.2:
    /// Z.ai trains/evaluates at temperature=1.0 — do NOT lower it for "coding"; leaving it unset
    /// is the recommended behaviour.</summary>
    double? Temperature = null,
    /// <summary>top_p passthrough. Null = omit (server default, 0.95 for GLM-5.2). Tune temperature
    /// OR top_p, never both.</summary>
    double? TopP = null,
    /// <summary>top_k passthrough (non-OpenAI; forwarded to llama.cpp / vLLM). Null = omit → server
    /// default. Auto-defaults to 20 for Qwen3 models (llama.cpp's built-in 40 is wrong for Qwen3).</summary>
    int? TopK = null,
    /// <summary>min_p passthrough (non-OpenAI; forwarded to llama.cpp / vLLM). Null = omit → server
    /// default. Auto-defaults to 0 for Qwen3 models (llama.cpp's built-in 0.05 is wrong for Qwen3).</summary>
    double? MinP = null,
    /// <summary>max_tokens (output cap) passthrough. Null = omit (uncapped up to the model's limit,
    /// 128K for GLM-5.2).</summary>
    int? MaxTokens = null,
    /// <summary>frequency_penalty passthrough (anti-repetition). Null = omit. Opt-in: fixes GLM's
    /// tendency to repeat tool calls at the source rather than at the app-layer loop detector.
    /// A light value (~0.1–0.3) is usually enough; do not stack with a heavy repetition_penalty.</summary>
    double? FrequencyPenalty = null,
    /// <summary>presence_penalty passthrough. Null = omit.</summary>
    double? PresencePenalty = null,
    /// <summary>Stream idle-watchdog timeout in seconds: abort the streaming read if the server sends
    /// no bytes for this long, instead of hanging forever under the (intentionally infinite) HTTP
    /// timeout. Resets on every chunk, so it trips only on a genuine stall. Null = client default
    /// (240s); &lt;= 0 = disabled (wait forever, the legacy behaviour).</summary>
    int? StreamIdleTimeoutSeconds = null,
    /// <summary>
    /// Generic escape hatch: arbitrary top-level request fields emitted VERBATIM (no snake_case
    /// rename), forwarded under <c>drop_params:false</c>. Absorbs provider-specific param drift
    /// (e.g. GLM <c>enable_thinking</c>, <c>top_k</c>, <c>chat_template_kwargs</c>) without a
    /// release. Load-bearing keys (model/messages/tools/stream/stream_options/drop_params) and the
    /// named passthroughs above always win — extra entries can never clobber them.
    /// </summary>
    ImmutableDictionary<string, JsonElement>? ExtraParams = null)
{
    public static LiteLLMSettings Empty { get; } = new(
        BaseUrl: null,
        ApiKey: null,
        TimeoutSeconds: null,
        ToolCallingMode: null,
        Models: ImmutableDictionary<string, string>.Empty,
        ContextWindows: ImmutableDictionary<string, int>.Empty,
        SubagentModels: ImmutableDictionary<string, string>.Empty,
        SmallFastModel: null,
        Vision: null,
        ReasoningEffort: null,
        Temperature: null,
        TopP: null,
        TopK: null,
        MinP: null,
        MaxTokens: null,
        FrequencyPenalty: null,
        PresencePenalty: null,
        StreamIdleTimeoutSeconds: null,
        ExtraParams: ImmutableDictionary<string, JsonElement>.Empty);

    public LiteLLMSettings Merge(LiteLLMSettings higher) => new(
        BaseUrl: higher.BaseUrl ?? BaseUrl,
        ApiKey: higher.ApiKey ?? ApiKey,
        TimeoutSeconds: higher.TimeoutSeconds ?? TimeoutSeconds,
        ToolCallingMode: higher.ToolCallingMode ?? ToolCallingMode,
        Models: EffectiveSettings.MergeOverride(Models, higher.Models),
        ContextWindows: EffectiveSettings.MergeOverride(ContextWindows, higher.ContextWindows),
        SubagentModels: EffectiveSettings.MergeOverride(SubagentModels, higher.SubagentModels),
        SmallFastModel: higher.SmallFastModel ?? SmallFastModel,
        Vision: higher.Vision ?? Vision,
        ReasoningEffort: higher.ReasoningEffort ?? ReasoningEffort,
        Temperature: higher.Temperature ?? Temperature,
        TopP: higher.TopP ?? TopP,
        TopK: higher.TopK ?? TopK,
        MinP: higher.MinP ?? MinP,
        MaxTokens: higher.MaxTokens ?? MaxTokens,
        FrequencyPenalty: higher.FrequencyPenalty ?? FrequencyPenalty,
        PresencePenalty: higher.PresencePenalty ?? PresencePenalty,
        StreamIdleTimeoutSeconds: higher.StreamIdleTimeoutSeconds ?? StreamIdleTimeoutSeconds,
        ExtraParams: EffectiveSettings.MergeOverride(
            ExtraParams ?? ImmutableDictionary<string, JsonElement>.Empty,
            higher.ExtraParams ?? ImmutableDictionary<string, JsonElement>.Empty));
}

/// <summary>
/// Top-level <c>"mcp"</c> section of settings.json. Currently carries only the per-server
/// initialise/handshake timeout; lives in its own record so future MCP-wide knobs (default
/// env vars, log directory, etc.) have an obvious home without bloating LiteLLMSettings.
/// </summary>
public sealed record McpSettings(int? InitTimeoutSeconds)
{
    public static McpSettings Empty { get; } = new(InitTimeoutSeconds: null);

    public McpSettings Merge(McpSettings higher) => new(
        InitTimeoutSeconds: higher.InitTimeoutSeconds ?? InitTimeoutSeconds);
}
