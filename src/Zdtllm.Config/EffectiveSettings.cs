using System.Collections.Immutable;

namespace Zdtllm.Config;

public sealed record EffectiveSettings(
    string? Model,
    PermissionsSettings Permissions,
    ImmutableDictionary<string, string> Env,
    LiteLLMSettings LiteLLM)
{
    public static EffectiveSettings Empty { get; } = new(
        Model: null,
        Permissions: PermissionsSettings.Empty,
        Env: ImmutableDictionary<string, string>.Empty,
        LiteLLM: LiteLLMSettings.Empty);

    public EffectiveSettings Merge(EffectiveSettings higher) => new(
        Model: higher.Model ?? Model,
        Permissions: Permissions.Merge(higher.Permissions),
        Env: MergeOverride(Env, higher.Env),
        LiteLLM: LiteLLM.Merge(higher.LiteLLM));

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
    ImmutableDictionary<string, string> SubagentModels)
{
    public static LiteLLMSettings Empty { get; } = new(
        BaseUrl: null,
        ApiKey: null,
        TimeoutSeconds: null,
        ToolCallingMode: null,
        Models: ImmutableDictionary<string, string>.Empty,
        ContextWindows: ImmutableDictionary<string, int>.Empty,
        SubagentModels: ImmutableDictionary<string, string>.Empty);

    public LiteLLMSettings Merge(LiteLLMSettings higher) => new(
        BaseUrl: higher.BaseUrl ?? BaseUrl,
        ApiKey: higher.ApiKey ?? ApiKey,
        TimeoutSeconds: higher.TimeoutSeconds ?? TimeoutSeconds,
        ToolCallingMode: higher.ToolCallingMode ?? ToolCallingMode,
        Models: EffectiveSettings.MergeOverride(Models, higher.Models),
        ContextWindows: EffectiveSettings.MergeOverride(ContextWindows, higher.ContextWindows),
        SubagentModels: EffectiveSettings.MergeOverride(SubagentModels, higher.SubagentModels));
}
