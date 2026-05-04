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

public sealed record LiteLLMClientOptions
{
    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
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
                    MaxTokens: ReadNullableInt(info, "max_tokens")));
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrEmpty(model);

        var bodyJson = SerializeRequest(messages, tools, model);

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
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < _options.MaxRetries)
            {
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // FINAL attempt timed out via HttpClient.Timeout (NOT via the user's CT). If we
                // didn't catch this, the raw TaskCanceledException would propagate up — being
                // an OperationCanceledException, the Repl would treat it as user cancellation
                // and print "(turn cancelled)" with no signal of what really happened. Capture
                // it so the loop falls through to the LiteLLMException wrap below.
                lastException = ex;
                break;
            }
        }

        throw new LiteLLMException(
            $"LiteLLM request failed after {_options.MaxRetries + 1} attempts: {lastException?.Message}",
            lastException);
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

    private static string SerializeRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDef>? tools,
        string model)
    {
        var payload = new RequestPayload
        {
            Model = model,
            Messages = messages.Select(ToWireMessage).ToList(),
            Tools = tools is { Count: > 0 } ? tools.Select(ToWireTool).ToList() : null,
            Stream = true,
            StreamOptions = new RequestStreamOptions(IncludeUsage: true),
            DropParams = false,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    private static RequestMessage ToWireMessage(ChatMessage m) => new()
    {
        Role = m.Role,
        Content = m.Content,
        ToolCalls = m.ToolCalls.IsDefaultOrEmpty
            ? null
            : m.ToolCalls
                .Select(tc => new RequestToolCall(tc.Id, "function",
                    new RequestFunctionCall(tc.FunctionName, tc.Arguments)))
                .ToList(),
        ToolCallId = m.ToolCallId,
    };

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
}

internal sealed record RequestStreamOptions(bool IncludeUsage);

internal sealed class RequestMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public List<RequestToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
}

internal sealed record RequestToolCall(string Id, string Type, RequestFunctionCall Function);

internal sealed record RequestFunctionCall(string Name, string Arguments);

internal sealed record RequestTool(string Type, RequestFunctionDef Function);

internal sealed record RequestFunctionDef(string Name, string Description, JsonElement Parameters);
