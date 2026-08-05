using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zdtllm.Config;

public sealed record SettingsLoadOptions
{
    public string? UserConfigPath { get; init; }
    public Func<string, string?>? EnvironmentReader { get; init; }
}

public static class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static EffectiveSettings LoadEffectiveSettings(string cwd, SettingsLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        options ??= new SettingsLoadOptions();
        var envRead = options.EnvironmentReader ?? Environment.GetEnvironmentVariable;
        var userPath = options.UserConfigPath ?? DefaultUserPath();
        var projectPath = Path.Combine(cwd, ".zdtllm", "settings.json");
        var localPath = Path.Combine(cwd, ".zdtllm", "settings.local.json");

        var user = LoadOne(userPath, envRead);
        var project = LoadOne(projectPath, envRead);
        var local = LoadOne(localPath, envRead);
        var envLayer = LoadEnvLayer(envRead);

        // Env vars layer on top of local settings (highest precedence below CLI args). This
        // matches the user-runtime-intent rule that env-injected ZDT_DEFAULT_*_MODEL pins
        // override anything previously declared in committed settings.json — same semantics
        // claude-cli applies to ANTHROPIC_DEFAULT_*_MODEL.
        return EffectiveSettings.Empty
            .Merge(user)
            .Merge(project)
            .Merge(local)
            .Merge(envLayer);
    }

    /// <summary>
    /// Build a synthetic <see cref="EffectiveSettings"/> layer from the ZDT_* env vars.
    /// Same role as claude-cli's <c>ANTHROPIC_BASE_URL</c> / <c>ANTHROPIC_AUTH_TOKEN</c> /
    /// <c>ANTHROPIC_DEFAULT_*_MODEL</c> / <c>ANTHROPIC_SMALL_FAST_MODEL</c>, but the var
    /// names use the project's canonical tier vocabulary
    /// (<c>light</c>/<c>medium</c>/<c>heavy</c>) so one naming convention covers
    /// settings.json, CLI flags, and env:
    ///
    /// <list type="bullet">
    ///   <item><c>ZDT_BASE_URL</c>             → <c>litellm.baseUrl</c></item>
    ///   <item><c>ZDT_API_KEY</c>              → <c>litellm.apiKey</c></item>
    ///   <item><c>ZDT_DEFAULT_HEAVY_MODEL</c>  → <c>litellm.models["heavy"]</c></item>
    ///   <item><c>ZDT_DEFAULT_MEDIUM_MODEL</c> → <c>litellm.models["medium"]</c></item>
    ///   <item><c>ZDT_DEFAULT_LIGHT_MODEL</c>  → <c>litellm.models["light"]</c></item>
    ///   <item><c>ZDT_SMALL_FAST_MODEL</c>     → <c>litellm.smallFastModel</c> (subagent fallback)</item>
    /// </list>
    ///
    /// The Models entries merge on top of any same-named key in settings.json, so a runtime
    /// <c>ZDT_DEFAULT_LIGHT_MODEL=foo</c> always wins over a committed <c>"light": "bar"</c>.
    /// </summary>
    private static EffectiveSettings LoadEnvLayer(Func<string, string?> envRead)
    {
        var modelsBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        AddIfPresent(envRead, "ZDT_DEFAULT_HEAVY_MODEL",  "heavy",  modelsBuilder);
        AddIfPresent(envRead, "ZDT_DEFAULT_MEDIUM_MODEL", "medium", modelsBuilder);
        AddIfPresent(envRead, "ZDT_DEFAULT_LIGHT_MODEL",  "light",  modelsBuilder);

        var smallFast = envRead("ZDT_SMALL_FAST_MODEL");
        var baseUrl   = envRead("ZDT_BASE_URL");
        var apiKey    = envRead("ZDT_API_KEY");

        var anyChange = modelsBuilder.Count > 0
            || !string.IsNullOrEmpty(smallFast)
            || !string.IsNullOrEmpty(baseUrl)
            || !string.IsNullOrEmpty(apiKey);
        if (!anyChange) return EffectiveSettings.Empty;

        var litellm = LiteLLMSettings.Empty with
        {
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
            Models = modelsBuilder.ToImmutable(),
            SmallFastModel = string.IsNullOrEmpty(smallFast) ? null : smallFast,
        };
        return EffectiveSettings.Empty with { LiteLLM = litellm };
    }

    private static void AddIfPresent(
        Func<string, string?> envRead,
        string envName,
        string aliasKey,
        ImmutableDictionary<string, string>.Builder builder)
    {
        var v = envRead(envName);
        if (!string.IsNullOrEmpty(v)) builder[aliasKey] = v;
    }

    private static string DefaultUserPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zdtllm",
        "settings.json");

    private static EffectiveSettings LoadOne(string path, Func<string, string?> envRead)
    {
        if (!File.Exists(path)) return EffectiveSettings.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            var raw = JsonSerializer.Deserialize<RawSettings>(stream, JsonOpts);
            return raw?.ToEffective(envRead) ?? EffectiveSettings.Empty;
        }
        catch (JsonException ex)
        {
            throw new SettingsLoadException($"Failed to parse settings file '{path}': {ex.Message}", ex);
        }
    }
}

public sealed class SettingsLoadException : Exception
{
    public SettingsLoadException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class RawSettings
{
    public string? Model { get; set; }
    public RawPermissions? Permissions { get; set; }
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("litellm")]
    public RawLiteLLM? LiteLLM { get; set; }

    [JsonPropertyName("mcp")]
    public RawMcp? Mcp { get; set; }

    public EffectiveSettings ToEffective(Func<string, string?> envRead) => new(
        Model: EnvironmentExpander.ExpandNullable(Model, envRead),
        Permissions: Permissions?.ToEffective(envRead) ?? PermissionsSettings.Empty,
        Env: ToEnvDict(Env, envRead),
        LiteLLM: LiteLLM?.ToEffective(envRead) ?? LiteLLMSettings.Empty,
        Mcp: Mcp?.ToEffective() ?? McpSettings.Empty);

    private static ImmutableDictionary<string, string> ToEnvDict(
        Dictionary<string, string>? src,
        Func<string, string?> envRead)
    {
        if (src is null || src.Count == 0) return ImmutableDictionary<string, string>.Empty;
        var b = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var kv in src)
            b[kv.Key] = EnvironmentExpander.Expand(kv.Value, envRead);
        return b.ToImmutable();
    }
}

internal sealed class RawPermissions
{
    public List<string>? Allow { get; set; }
    public List<string>? Ask { get; set; }
    public List<string>? Deny { get; set; }
    public List<string>? AdditionalDirectories { get; set; }
    public string? DefaultMode { get; set; }

    public PermissionsSettings ToEffective(Func<string, string?> envRead) => new(
        Allow: ToImmArr(Allow, envRead),
        Ask: ToImmArr(Ask, envRead),
        Deny: ToImmArr(Deny, envRead),
        AdditionalDirectories: ToImmArr(AdditionalDirectories, envRead),
        DefaultMode: EnvironmentExpander.ExpandNullable(DefaultMode, envRead));

    private static ImmutableArray<string> ToImmArr(List<string>? list, Func<string, string?> envRead)
    {
        if (list is null || list.Count == 0) return ImmutableArray<string>.Empty;
        var b = ImmutableArray.CreateBuilder<string>(list.Count);
        foreach (var s in list)
            b.Add(EnvironmentExpander.Expand(s, envRead));
        return b.ToImmutable();
    }
}

internal sealed class RawLiteLLM
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string? ToolCallingMode { get; set; }
    public Dictionary<string, string>? Models { get; set; }
    public Dictionary<string, int>? ContextWindows { get; set; }

    /// <summary>
    /// Optional subagent-type → tier-alias OR model-id map. Lets the user assign a different
    /// model per subagent profile (e.g. <c>"code-reviewer": "light"</c> so the read-only
    /// reviewer runs on the cheap tier while the parent stays on medium / heavy). The value
    /// can be either an alias from <see cref="LiteLLMSettings.Models"/> or a model id directly.
    /// When unset, sensible defaults apply (see <c>SubagentModelResolver</c>).
    /// </summary>
    public Dictionary<string, string>? SubagentModels { get; set; }

    /// <summary>Optional vision override — see <see cref="LiteLLMSettings.Vision"/>.</summary>
    public bool? Vision { get; set; }

    /// <summary>Reasoning-effort passthrough — see <see cref="LiteLLMSettings.ReasoningEffort"/>.
    /// For GLM-5.2 use <c>"high"</c> for routine coding, <c>"max"</c> for hard planning.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Sampling temperature passthrough — see <see cref="LiteLLMSettings.Temperature"/>.</summary>
    public double? Temperature { get; set; }

    /// <summary>top_p passthrough — see <see cref="LiteLLMSettings.TopP"/>.</summary>
    public double? TopP { get; set; }

    /// <summary>top_k passthrough — see <see cref="LiteLLMSettings.TopK"/>.</summary>
    public int? TopK { get; set; }

    /// <summary>min_p passthrough — see <see cref="LiteLLMSettings.MinP"/>.</summary>
    public double? MinP { get; set; }

    /// <summary>max_tokens output-cap passthrough — see <see cref="LiteLLMSettings.MaxTokens"/>.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>frequency_penalty passthrough — see <see cref="LiteLLMSettings.FrequencyPenalty"/>.</summary>
    public double? FrequencyPenalty { get; set; }

    /// <summary>presence_penalty passthrough — see <see cref="LiteLLMSettings.PresencePenalty"/>.</summary>
    public double? PresencePenalty { get; set; }

    /// <summary>Verbatim extra request fields — see <see cref="LiteLLMSettings.ExtraParams"/>.</summary>
    public Dictionary<string, JsonElement>? ExtraParams { get; set; }

    public LiteLLMSettings ToEffective(Func<string, string?> envRead) => new(
        BaseUrl: EnvironmentExpander.ExpandNullable(BaseUrl, envRead),
        ApiKey: EnvironmentExpander.ExpandNullable(ApiKey, envRead),
        TimeoutSeconds: TimeoutSeconds,
        ToolCallingMode: EnvironmentExpander.ExpandNullable(ToolCallingMode, envRead),
        Models: ToStringDict(Models, envRead),
        ContextWindows: ToIntDict(ContextWindows),
        SubagentModels: ToStringDict(SubagentModels, envRead),
        // SmallFastModel comes only from env (ZDT_SMALL_FAST_MODEL); RawLiteLLM doesn't
        // expose a settings.json key for it. Keep it null here and let the env layer in
        // LoadEffectiveSettings populate it.
        SmallFastModel: null,
        Vision: Vision,
        ReasoningEffort: EnvironmentExpander.ExpandNullable(ReasoningEffort, envRead),
        Temperature: Temperature,
        TopP: TopP,
        TopK: TopK,
        MinP: MinP,
        MaxTokens: MaxTokens,
        FrequencyPenalty: FrequencyPenalty,
        PresencePenalty: PresencePenalty,
        ExtraParams: ToJsonElementDict(ExtraParams));

    private static ImmutableDictionary<string, string> ToStringDict(
        Dictionary<string, string>? src,
        Func<string, string?> envRead)
    {
        if (src is null || src.Count == 0) return ImmutableDictionary<string, string>.Empty;
        var b = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var kv in src)
            b[kv.Key] = EnvironmentExpander.Expand(kv.Value, envRead);
        return b.ToImmutable();
    }

    private static ImmutableDictionary<string, int> ToIntDict(Dictionary<string, int>? src)
    {
        if (src is null || src.Count == 0) return ImmutableDictionary<string, int>.Empty;
        var b = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var kv in src) b[kv.Key] = kv.Value;
        return b.ToImmutable();
    }

    private static ImmutableDictionary<string, JsonElement> ToJsonElementDict(Dictionary<string, JsonElement>? src)
    {
        if (src is null || src.Count == 0) return ImmutableDictionary<string, JsonElement>.Empty;
        var b = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        // Clone each element so it survives disposal of the source JsonDocument.
        foreach (var kv in src) b[kv.Key] = kv.Value.Clone();
        return b.ToImmutable();
    }
}

internal sealed class RawMcp
{
    /// <summary>
    /// Per-server initialise/handshake timeout in seconds. Replaces the previous hard-coded
    /// 15 s — slow-booting MCP servers (Laravel/Django on Windows + Herd, cold caches, DB-
    /// dependent auth) routinely need more. CLI flag <c>--mcp-init-timeout-seconds</c> wins
    /// over this; both fall back to 15 s if neither is set.
    /// </summary>
    public int? InitTimeoutSeconds { get; set; }

    public McpSettings ToEffective() => new(
        InitTimeoutSeconds: InitTimeoutSeconds);
}
