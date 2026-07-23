using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zdtllm.LiteLLM;

public sealed class LiteLLMException : Exception
{
    public LiteLLMException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Thrown when the upstream proxy returns HTTP 429 and we've exhausted retries — or when
/// the proxy explicitly told us the bucket won't reset in time. Carries the Unix
/// timestamp at which the rate-limit window resets so observers (stream-json) and the
/// REPL can surface a useful "try again at HH:MM" message instead of just "request failed".
/// </summary>
public sealed class RateLimitException : Exception
{
    /// <summary>Unix-seconds timestamp when the rate-limit window is expected to reset.
    /// May be null when the upstream provided no Retry-After or x-ratelimit-reset hint;
    /// callers should default to a sensible fallback (e.g. now + 1h).</summary>
    public long? ResetsAtUnix { get; }

    public RateLimitException(string message, long? resetsAtUnix, Exception? inner = null)
        : base(message, inner)
    {
        ResetsAtUnix = resetsAtUnix;
    }
}

public sealed record LiteLLMClientOptions
{
    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Optional request-shaping passthroughs. All null/empty by default so an
    /// unconfigured client serializes a byte-for-byte identical request body (the actual
    /// safety guarantee — <c>drop_params:false</c> forwards unknown params, it does not drop
    /// them). See the matching <c>LiteLLMSettings</c> members for the GLM-5.2 guidance.</summary>
    public string? ReasoningEffort { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxTokens { get; init; }
    /// <summary>frequency_penalty passthrough — anti-repetition lever. Null = omit. Fixes GLM's
    /// tendency to repeat tool calls at the source rather than at the app-layer loop detector.</summary>
    public double? FrequencyPenalty { get; init; }
    /// <summary>presence_penalty passthrough. Null = omit.</summary>
    public double? PresencePenalty { get; init; }
    /// <summary>Verbatim extra top-level fields; can never clobber load-bearing keys.</summary>
    public IReadOnlyDictionary<string, JsonElement>? ExtraParams { get; init; }
}

public sealed class LiteLLMClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly LiteLLMClientOptions _options;

    public LiteLLMClient(HttpClient http, LiteLLMClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
    }

    /// <summary>The client's configured reasoning-effort passthrough (null when unset). Callers use
    /// this to decide whether a per-turn escalation (think/ultrathink keywords) is meaningful — a
    /// model with no base reasoning_effort should not be sent one.</summary>
    public string? ReasoningEffort => _options.ReasoningEffort;

    /// <summary>
    /// Calls the LiteLLM proxy's /model/info admin route and parses the response into
    /// ModelInfo records. Returns an empty list (rather than throwing) on any failure
    /// — callers fall back to settings.contextWindows. Honors a 10s timeout independent
    /// of the configured RequestTimeout because /model/info shouldn't gate startup.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> GetModelInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_options.BaseUrl.TrimEnd('/')}/model/info";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<ModelInfo>();

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<ModelInfo>();

            var result = new List<ModelInfo>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var modelName = item.TryGetProperty("model_name", out var mn) && mn.ValueKind == JsonValueKind.String
                    ? mn.GetString() : null;
                if (string.IsNullOrEmpty(modelName)) continue;

                var info = item.TryGetProperty("model_info", out var mi) && mi.ValueKind == JsonValueKind.Object ? mi : default;
                result.Add(new ModelInfo(
                    modelName,
                    MaxInputTokens: ReadNullableInt(info, "max_input_tokens"),
                    MaxOutputTokens: ReadNullableInt(info, "max_output_tokens"),
                    MaxTokens: ReadNullableInt(info, "max_tokens"),
                    SupportsVision: ReadNullableBool(info, "supports_vision")));
            }
            return result;
        }
        catch
        {
            return Array.Empty<ModelInfo>();
        }
    }

    private static int? ReadNullableInt(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }

    private static bool? ReadNullableBool(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>
    /// Single non-streaming completion. Internally drains StreamChatAsync and concatenates
    /// every TextDelta into one string. Tools are not sent (this is meant for one-shot
    /// summarisation / classification calls). Returns the full text after the model emits
    /// its Done chunk.
    /// </summary>
    public async Task<string> GetCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamChatAsync(messages, tools: null, model, ct).ConfigureAwait(false))
        {
            if (chunk is ChatChunk.TextDelta td) sb.Append(td.Text);
        }
        return sb.ToString();
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDef>? tools,
        string model,
        [EnumeratorCancellation] CancellationToken ct = default,
        string? reasoningEffortOverride = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrEmpty(model);

        var bodyJson = SerializeRequest(messages, tools, model, reasoningEffortOverride);
        var response = await SendWithRetryAsync(bodyJson, ct).ConfigureAwait(false);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var chunk in SseParser.ParseAsync(stream, ct).ConfigureAwait(false))
                yield return chunk;
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string bodyJson, CancellationToken ct)
    {
        Exception? lastException = null;
        // Track the most recent 429 reset hint across retries — if every attempt 429s, we
        // surface a structured RateLimitException at the end instead of a generic wrap.
        long? lastRateLimitResetsAt = null;
        bool lastWasRateLimit = false;

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(ComputeBackoff(attempt - 1), ct).ConfigureAwait(false);

            // Each retry builds a fresh request (HttpRequestMessage isn't re-sendable). Wrap
            // in a `using` so the message + body StringContent get disposed regardless of
            // whether SendAsync threw, returned a retryable status, or succeeded — successful
            // sends transfer ownership of the response to the caller, but the request itself
            // is always safe to dispose post-send.
            using var request = BuildRequest(bodyJson);

            try
            {
                var response = await _http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return response;

                if (IsRetryableStatus(response.StatusCode))
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Pull the reset hint from the headers BEFORE disposing the response.
                        // Retry-After is the standard claude/Anthropic-style hint; some
                        // proxies (LiteLLM with Anthropic upstream) also forward
                        // x-ratelimit-reset as a Unix-seconds value — check both.
                        lastRateLimitResetsAt = ParseRateLimitResetUnix(response);
                        lastWasRateLimit = true;
                    }
                    else
                    {
                        lastWasRateLimit = false;
                    }

                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    response.Dispose();
                    lastException = new LiteLLMException(
                        $"LiteLLM HTTP {(int)response.StatusCode} (retryable): {Truncate(body)}");
                    continue;
                }

                var failBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                throw new LiteLLMException(
                    $"LiteLLM HTTP {(int)response.StatusCode}: {Truncate(failBody)}");
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetries)
            {
                lastException = ex;
                lastWasRateLimit = false;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < _options.MaxRetries)
            {
                lastException = ex;
                lastWasRateLimit = false;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // FINAL attempt timed out via HttpClient.Timeout (NOT via the user's CT). If we
                // didn't catch this, the raw TaskCanceledException would propagate up — being
                // an OperationCanceledException, the Repl would treat it as user cancellation
                // and print "(turn cancelled)" with no signal of what really happened. Capture
                // it so the loop falls through to the LiteLLMException wrap below.
                lastException = ex;
                lastWasRateLimit = false;
                break;
            }
        }

        if (lastWasRateLimit)
        {
            var resetIso = lastRateLimitResetsAt is long unix
                ? DateTimeOffset.FromUnixTimeSeconds(unix).ToString("u")
                : "unknown";
            throw new RateLimitException(
                $"LiteLLM rate limit exceeded after {_options.MaxRetries + 1} attempts (resets at {resetIso}).",
                lastRateLimitResetsAt,
                lastException);
        }

        throw new LiteLLMException(
            $"LiteLLM request failed after {_options.MaxRetries + 1} attempts: {lastException?.Message}",
            lastException);
    }

    /// <summary>
    /// Pull a Unix-seconds reset timestamp out of HTTP 429 response headers. Tries — in
    /// order — Retry-After (delta-seconds OR HTTP-date), then x-ratelimit-reset (epoch
    /// seconds, the Anthropic / OpenAI form). Returns null when no usable hint is present;
    /// the caller falls back to a sensible default (e.g. +1h) on the consumer side.
    /// </summary>
    private static long? ParseRateLimitResetUnix(HttpResponseMessage response)
    {
        // Retry-After: delta-seconds form → +N seconds from now.
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is TimeSpan delta && delta > TimeSpan.Zero)
                return DateTimeOffset.UtcNow.Add(delta).ToUnixTimeSeconds();
            if (ra.Date is DateTimeOffset abs && abs > DateTimeOffset.UtcNow)
                return abs.ToUnixTimeSeconds();
        }

        // x-ratelimit-reset: Unix-seconds (Anthropic/OpenAI convention). Some proxies emit it
        // as a string; HttpClient surfaces it via TryGetValues since it's not strongly typed.
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values))
        {
            foreach (var v in values)
            {
                if (long.TryParse(v, out var unix) && unix > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return unix;
            }
        }

        return null;
    }

    private HttpRequestMessage BuildRequest(string bodyJson)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private TimeSpan ComputeBackoff(int retryIndex)
    {
        var baseMs = _options.InitialBackoff.TotalMilliseconds * Math.Pow(2, retryIndex);
        var jitter = (Random.Shared.NextDouble() * 0.2) - 0.1;
        return TimeSpan.FromMilliseconds(baseMs * (1 + jitter));
    }

    private static bool IsRetryableStatus(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static string Truncate(string s) =>
        s.Length <= 1024 ? s : string.Concat(s.AsSpan(0, 1024), "…");

    // Instance (not static) so it can read the optional passthroughs on _options. Reserved keys
    // that extraParams may never overwrite — the load-bearing request shape plus the named
    // passthroughs below (which, when set, are already on the payload and must win).
    private static readonly HashSet<string> ProtectedRequestKeys = new(StringComparer.Ordinal)
    {
        "model", "messages", "tools", "stream", "stream_options", "drop_params",
        "reasoning_effort", "temperature", "top_p", "max_tokens",
        "frequency_penalty", "presence_penalty",
    };

    private string SerializeRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDef>? tools,
        string model,
        string? reasoningEffortOverride = null)
    {
        var payload = new RequestPayload
        {
            Model = model,
            Messages = messages.Select(ToWireMessage).ToList(),
            Tools = tools is { Count: > 0 } ? tools.Select(ToWireTool).ToList() : null,
            Stream = true,
            StreamOptions = new RequestStreamOptions(IncludeUsage: true),
            DropParams = false,
            // Nullable — WhenWritingNull drops them, so an unconfigured client is byte-identical.
            // A per-turn override (think/ultrathink keyword) wins over the configured base.
            ReasoningEffort = reasoningEffortOverride ?? _options.ReasoningEffort,
            Temperature = _options.Temperature,
            TopP = _options.TopP,
            MaxTokens = _options.MaxTokens,
            FrequencyPenalty = _options.FrequencyPenalty,
            PresencePenalty = _options.PresencePenalty,
        };

        // Fast path: no extra params → keep the exact historical serialization (and output bytes).
        if (_options.ExtraParams is not { Count: > 0 } extra)
            return JsonSerializer.Serialize(payload, JsonOpts);

        // Merge verbatim extra fields onto the typed payload. Load-bearing / named keys always win:
        // a key already present on the node (structural fields, or a set named passthrough) or on the
        // reserved list is skipped, so extraParams can never break streaming, usage, or tool routing.
        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        foreach (var kv in extra)
        {
            if (ProtectedRequestKeys.Contains(kv.Key)) continue;
            if (node.ContainsKey(kv.Key)) continue;
            node[kv.Key] = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonNode>(kv.Value.GetRawText());
        }
        return node.ToJsonString(JsonOpts);
    }

    private static RequestMessage ToWireMessage(ChatMessage m) => new()
    {
        Role = m.Role,
        Content = BuildContent(m),
        ToolCalls = m.ToolCalls.IsDefaultOrEmpty
            ? null
            : m.ToolCalls
                .Select(tc => new RequestToolCall(tc.Id, "function",
                    new RequestFunctionCall(tc.FunctionName, tc.Arguments)))
                .ToList(),
        ToolCallId = m.ToolCallId,
    };

    /// <summary>
    /// Build a message's <c>content</c> value. Plain text stays a JSON string (every model
    /// understands it). When the message carries image attachments, content becomes the OpenAI
    /// multimodal array — a text part (if any) followed by one <c>image_url</c> part per image —
    /// which LiteLLM routes to vision-capable models. Only user turns ever carry images, so
    /// non-vision paths are byte-for-byte unchanged.
    /// </summary>
    private static object? BuildContent(ChatMessage m)
    {
        if (m.Images.IsDefaultOrEmpty) return m.Content;

        var parts = new List<object>(m.Images.Length + 1);
        if (!string.IsNullOrEmpty(m.Content))
            parts.Add(new { type = "text", text = m.Content });
        foreach (var img in m.Images)
            parts.Add(new { type = "image_url", image_url = new { url = img } });
        return parts;
    }

    private static RequestTool ToWireTool(ToolDef t) =>
        new("function", new RequestFunctionDef(t.Name, t.Description, t.Parameters));
}

internal sealed class RequestPayload
{
    public required string Model { get; init; }
    public required List<RequestMessage> Messages { get; init; }
    public List<RequestTool>? Tools { get; init; }
    public bool Stream { get; init; }
    public RequestStreamOptions? StreamOptions { get; init; }
    public bool DropParams { get; init; }
    // Optional passthroughs — snake_cased by JsonOpts; dropped when null (WhenWritingNull).
    public string? ReasoningEffort { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxTokens { get; init; }
    public double? FrequencyPenalty { get; init; }
    public double? PresencePenalty { get; init; }
}

internal sealed record RequestStreamOptions(bool IncludeUsage);

internal sealed class RequestMessage
{
    public required string Role { get; init; }
    // string for the text-only case, or a List<object> of content parts for multimodal (images).
    public object? Content { get; init; }
    public List<RequestToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
}

internal sealed record RequestToolCall(string Id, string Type, RequestFunctionCall Function);

internal sealed record RequestFunctionCall(string Name, string Arguments);

internal sealed record RequestTool(string Type, RequestFunctionDef Function);

internal sealed record RequestFunctionDef(string Name, string Description, JsonElement Parameters);
