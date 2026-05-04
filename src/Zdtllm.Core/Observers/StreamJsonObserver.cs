using System.Text.Json;

namespace Zdtllm.Core.Observers;

/// <summary>
/// Streams the agent's events as newline-delimited JSON (NDJSON) to a TextWriter.
/// One line per event so consumers can parse incrementally — useful when piping zdt
/// output into other CLI tools, or wiring a UI on top of <c>zdt -p --output-format
/// stream-json</c>. Event types: text_delta, tool_call, tool_result, final.
/// </summary>
public sealed class StreamJsonObserver : IAgentObserver
{
    private readonly TextWriter _sink;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StreamJsonObserver(TextWriter sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    public async Task OnTextDeltaAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return;
        await EmitAsync(new { type = "text_delta", text }, ct).ConfigureAwait(false);
    }

    public async Task OnToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        // Try to embed the raw arguments as a JSON object/value rather than a string —
        // consumers shouldn't have to JSON.parse a string they just received.
        object args = TryParse(argumentsJson) ?? (object)argumentsJson;
        await EmitAsync(new { type = "tool_call", name = toolName, arguments = args }, ct).ConfigureAwait(false);
    }

    public async Task OnToolResultAsync(string toolName, string content, bool isError, TimeSpan duration, CancellationToken ct) =>
        await EmitAsync(new
        {
            type = "tool_result",
            name = toolName,
            content,
            is_error = isError,
            duration_ms = (long)duration.TotalMilliseconds,
        }, ct).ConfigureAwait(false);

    public async Task OnFinalAsync(string finalText, int turns, int? promptTokens, int? completionTokens, CancellationToken ct) =>
        await EmitAsync(new
        {
            type = "final",
            text = finalText,
            turns,
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
        }, ct).ConfigureAwait(false);

    private async Task EmitAsync(object payload, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(payload, JsonOpts);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _sink.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _sink.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static object? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
