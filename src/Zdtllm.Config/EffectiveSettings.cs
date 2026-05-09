using System.Collections.Immutable;

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
    string? SmallFastModel)
{
    public static LiteLLMSettings Empty { get; } = new(
        BaseUrl: null,
        ApiKey: null,
        TimeoutSeconds: null,
        ToolCallingMode: null,
        Models: ImmutableDictionary<string, string>.Empty,
        ContextWindows: ImmutableDictionary<string, int>.Empty,
        SubagentModels: ImmutableDictionary<string, string>.Empty,
        SmallFastModel: null);

    public LiteLLMSettings Merge(LiteLLMSettings higher) => new(
        BaseUrl: higher.BaseUrl ?? BaseUrl,
        ApiKey: higher.ApiKey ?? ApiKey,
        TimeoutSeconds: higher.TimeoutSeconds ?? TimeoutSeconds,
        ToolCallingMode: higher.ToolCallingMode ?? ToolCallingMode,
        Models: EffectiveSettings.MergeOverride(Models, higher.Models),
        ContextWindows: EffectiveSettings.MergeOverride(ContextWindows, higher.ContextWindows),
        SubagentModels: EffectiveSettings.MergeOverride(SubagentModels, higher.SubagentModels),
        SmallFastModel: higher.SmallFastModel ?? SmallFastModel);
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
