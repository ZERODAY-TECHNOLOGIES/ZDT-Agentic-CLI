using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zdtllm.Core.Setup;

/// <summary>
/// First-run interactive wizard. Prompts the user for LiteLLM endpoint, API key,
/// and a model per tier (light / medium / heavy), then writes settings.json.
/// All I/O goes through the supplied TextReader / TextWriter so the wizard is
/// unit-testable against a StringReader (no real Console).
/// </summary>
public sealed class SetupWizard
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly HttpClient _http;

    public SetupWizard(TextReader input, TextWriter output, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(http);
        _input = input;
        _output = output;
        _http = http;
    }

    public static string DefaultUserSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zdtllm",
        "settings.json");

    public async Task<WizardResult> RunAsync(string targetPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        await PrintWelcomeAsync(targetPath).ConfigureAwait(false);

        var baseUrl = await PromptAsync(
            "LiteLLM endpoint URL",
            defaultValue: "http://localhost:4000",
            validate: TryParseUrl,
            ct: ct).ConfigureAwait(false);

        var apiKey = await PromptAsync(
            "API key (or ${VAR} to read from env at runtime; leave blank for none)",
            defaultValue: null,
            allowEmpty: true,
            ct: ct).ConfigureAwait(false);

        var discovered = await DiscoverModelsAsync(baseUrl, apiKey, ct).ConfigureAwait(false);
        await PrintDiscoveryAsync(baseUrl, discovered).ConfigureAwait(false);

        var lightModel = await PickModelAsync("light", "small / cheap / fast", discovered, ct).ConfigureAwait(false);
        var mediumModel = await PickModelAsync("medium", "default workhorse", discovered, ct).ConfigureAwait(false);
        var heavyModel = await PickModelAsync("heavy", "large / smart / slow", discovered, ct).ConfigureAwait(false);

        var suggestedMode = SuggestMode(lightModel, mediumModel, heavyModel);
        var modeStr = await PromptAsync(
            $"Tool calling mode — recommended for these models: {suggestedMode}",
            defaultValue: suggestedMode,
            validate: ValidateMode,
            ct: ct).ConfigureAwait(false);

        var defaultAlias = await PromptAsync(
            "Default model alias (light / medium / heavy)",
            defaultValue: "medium",
            validate: ValidateAlias,
            ct: ct).ConfigureAwait(false);

        var (settingsJson, mergedExisting) = BuildSettingsJson(
            targetPath, baseUrl, apiKey, defaultAlias, modeStr,
            lightModel, mediumModel, heavyModel);

        await _output.WriteLineAsync().ConfigureAwait(false);
        await _output.WriteLineAsync(
            mergedExisting
                ? $"Will UPDATE {targetPath} (preserving any other top-level keys):"
                : $"Will CREATE {targetPath}:")
            .ConfigureAwait(false);
        await _output.WriteLineAsync(settingsJson).ConfigureAwait(false);

        var confirm = await PromptAsync(
            "Write this file? (Y/n)",
            defaultValue: "Y",
            allowEmpty: false,
            ct: ct).ConfigureAwait(false);

        if (!confirm.StartsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            await _output.WriteLineAsync("Aborted; nothing written.").ConfigureAwait(false);
            return new WizardResult(targetPath, UserConfirmed: false);
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(targetPath, settingsJson, ct).ConfigureAwait(false);

        await _output.WriteLineAsync($"Saved {targetPath}").ConfigureAwait(false);
        await _output.WriteLineAsync().ConfigureAwait(false);
        await _output.WriteLineAsync("zdt is ready. Try:  zdt -p \"hello\"").ConfigureAwait(false);
        return new WizardResult(targetPath, UserConfirmed: true);
    }

    private async Task PrintWelcomeAsync(string targetPath)
    {
        await _output.WriteLineAsync("─── zdtllmcli setup ───────────────────────────────").ConfigureAwait(false);
        await _output.WriteLineAsync($"  Writes config to: {targetPath}").ConfigureAwait(false);
        await _output.WriteLineAsync("  You can edit it by hand later — this just gives you a starting point.").ConfigureAwait(false);
        await _output.WriteLineAsync("───────────────────────────────────────────────────").ConfigureAwait(false);
        await _output.WriteLineAsync().ConfigureAwait(false);
    }

    private async Task PrintDiscoveryAsync(string baseUrl, string[]? discovered)
    {
        await _output.WriteLineAsync().ConfigureAwait(false);
        if (discovered is null)
        {
            await _output.WriteLineAsync(
                $"(could not connect to {baseUrl}/v1/models — that's fine, you can type model names by hand)")
                .ConfigureAwait(false);
        }
        else if (discovered.Length == 0)
        {
            await _output.WriteLineAsync(
                $"(connected to {baseUrl}, but no models advertised — you'll need to type names by hand)")
                .ConfigureAwait(false);
        }
        else
        {
            await _output.WriteLineAsync($"Found {discovered.Length} model(s) at {baseUrl}:").ConfigureAwait(false);
            for (var i = 0; i < discovered.Length; i++)
                await _output.WriteLineAsync($"  {i + 1}) {discovered[i]}").ConfigureAwait(false);
        }
        await _output.WriteLineAsync().ConfigureAwait(false);
    }

    private async Task<string> PickModelAsync(
        string tier, string description, string[]? available, CancellationToken ct)
    {
        var hint = available is { Length: > 0 }
            ? $"  (enter a number 1-{available.Length}, or a custom model name)"
            : "  (enter a model name)";

        while (true)
        {
            await _output.WriteLineAsync($"Model for the {tier} tier — {description}").ConfigureAwait(false);
            await _output.WriteLineAsync(hint).ConfigureAwait(false);
            await _output.WriteAsync("> ").ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);

            var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new OperationCanceledException("Input ended during setup wizard.");

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (available is { Length: > 0 }
                && int.TryParse(trimmed, out var idx)
                && idx >= 1 && idx <= available.Length)
            {
                return available[idx - 1];
            }
            return trimmed;
        }
    }

    private async Task<string> PromptAsync(
        string question,
        string? defaultValue,
        Func<string, bool>? validate = null,
        bool allowEmpty = false,
        CancellationToken ct = default)
    {
        var defaultPart = string.IsNullOrEmpty(defaultValue) ? string.Empty : $" [{defaultValue}]";

        while (true)
        {
            await _output.WriteLineAsync(question + defaultPart).ConfigureAwait(false);
            await _output.WriteAsync("> ").ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);

            var line = await _input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new OperationCanceledException("Input ended during setup wizard.");

            var raw = line.Trim();
            var value = raw.Length == 0 ? (defaultValue ?? string.Empty) : raw;

            if (string.IsNullOrEmpty(value) && !allowEmpty) continue;
            if (validate is not null && !validate(value)) continue;
            return value;
        }
    }

    private async Task<string[]?> DiscoverModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/v1/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            // Don't try to expand ${VAR} here — at wizard time the env var may not yet be set
            // and that's fine; we just won't authenticate the discovery call.
            if (!string.IsNullOrEmpty(apiKey) && !apiKey.Contains("${", StringComparison.Ordinal))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            return data.EnumerateArray()
                .Where(e => e.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                .Select(e => e.GetProperty("id").GetString()!)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static (string Json, bool MergedExisting) BuildSettingsJson(
        string targetPath,
        string baseUrl,
        string apiKey,
        string defaultAlias,
        string mode,
        string lightModel,
        string mediumModel,
        string heavyModel)
    {
        var litellm = new JsonObject
        {
            ["baseUrl"] = baseUrl,
            ["toolCallingMode"] = mode,
            ["models"] = new JsonObject
            {
                ["light"] = lightModel,
                ["medium"] = mediumModel,
                ["heavy"] = heavyModel,
            },
        };
        if (!string.IsNullOrEmpty(apiKey))
            litellm["apiKey"] = apiKey;

        JsonObject root;
        var mergedExisting = false;
        if (File.Exists(targetPath))
        {
            try
            {
                var existing = File.ReadAllText(targetPath);
                if (JsonNode.Parse(existing) is JsonObject parsed)
                {
                    root = parsed;
                    mergedExisting = true;
                }
                else
                {
                    root = new JsonObject();
                }
            }
            catch (JsonException)
            {
                // Existing file is malformed — skip merging rather than overwriting silently.
                root = new JsonObject();
                mergedExisting = false;
            }
        }
        else
        {
            root = new JsonObject();
        }

        root["model"] = defaultAlias;
        root["litellm"] = litellm;

        return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), mergedExisting);
    }

    // Uses the SAME predicate as the runtime resolver (ModelHeuristics) so the wizard's suggestion
    // and the runtime fallback can never disagree — notably, both now suggest native for GLM.
    internal static string SuggestMode(params string[] models) =>
        models.Any(ModelHeuristics.LooksLikeXmlOnly) ? "xml" : "native";

    private static bool TryParseUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static bool ValidateMode(string s) =>
        s.Equals("native", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("xml", StringComparison.OrdinalIgnoreCase);

    private static bool ValidateAlias(string s)
    {
        // Accept the canonical aliases. Anything else is allowed as a free-form alias too,
        // but we coach the user toward the expected ones.
        var t = s.ToLowerInvariant();
        return t == "light" || t == "medium" || t == "heavy";
    }
}

public sealed record WizardResult(string SettingsPath, bool UserConfirmed);
