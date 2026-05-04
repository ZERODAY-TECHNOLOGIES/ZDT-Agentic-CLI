using System.Text;
using System.Text.Json;

namespace Zdtllm.Core.Observers;

/// <summary>
/// Buffers every event the agent emits and writes ONE pretty JSON object to the sink
/// when <see cref="EmitAsync"/> is called (typically right before the CLI exits in
/// <c>-p --output-format json</c> mode). Consumers get a single self-describing payload
/// instead of having to reassemble the NDJSON stream.
/// </summary>
public sealed class AggregatingJsonObserver : IAgentObserver
{
    private readonly StringBuilder _accumulatedText = new();
    private readonly List<ToolEvent> _toolEvents = new();
    // Per-tool-name FIFO of un-paired call entries — lets OnToolResultAsync match in O(1)
    // instead of doing a linear LastOrDefault scan over every accumulated event. Matters
    // for long sessions where _toolEvents grows past a few hundred entries.
    private readonly Dictionary<string, Queue<ToolEvent>> _pendingByName = new(StringComparer.Ordinal);
    private string? _finalText;
    private int _turns;
    private int? _promptTokens;
    private int? _completionTokens;

    private readonly object _gate = new();

    public Task OnTextDeltaAsync(string text, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(text))
        {
            lock (_gate) _accumulatedText.Append(text);
        }
        return Task.CompletedTask;
    }

    public Task OnToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var entry = new ToolEvent
        {
            Name = toolName,
            ArgumentsRaw = argumentsJson,
        };
        lock (_gate)
        {
            _toolEvents.Add(entry);
            if (!_pendingByName.TryGetValue(toolName, out var queue))
                _pendingByName[toolName] = queue = new Queue<ToolEvent>();
            queue.Enqueue(entry);
        }
        return Task.CompletedTask;
    }

    public Task OnToolResultAsync(string toolName, string content, bool isError, TimeSpan duration, CancellationToken ct)
    {
        lock (_gate)
        {
            // O(1) FIFO match — pop the oldest un-completed call for this tool name. Within a
            // parallel batch, results may arrive in a different order than calls, but pairing
            // by FIFO-per-name still gives every call exactly one result and vice versa.
            if (_pendingByName.TryGetValue(toolName, out var queue) && queue.TryDequeue(out var pending))
            {
                pending.Content = content;
                pending.IsError = isError;
                pending.DurationMs = (long)duration.TotalMilliseconds;
                pending.Completed = true;
            }
            else
            {
                _toolEvents.Add(new ToolEvent
                {
                    Name = toolName,
                    Content = content,
                    IsError = isError,
                    DurationMs = (long)duration.TotalMilliseconds,
                    Completed = true,
                });
            }
        }
        return Task.CompletedTask;
    }

    public Task OnFinalAsync(string finalText, int turns, int? promptTokens, int? completionTokens, CancellationToken ct)
    {
        lock (_gate)
        {
            _finalText = finalText;
            _turns = turns;
            _promptTokens = promptTokens;
            _completionTokens = completionTokens;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Render the buffered events as a single pretty JSON object and flush to <paramref name="sink"/>.
    /// Call once after RunTurnAsync returns. Idempotent isn't a concern — the typical caller is the
    /// CLI exit path which only fires once.
    /// </summary>
    public async Task EmitAsync(TextWriter sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);

        object payload;
        lock (_gate)
        {
            payload = new
            {
                result = _finalText ?? _accumulatedText.ToString(),
                turns = _turns,
                prompt_tokens = _promptTokens,
                completion_tokens = _completionTokens,
                tool_calls = _toolEvents.Select(e => new
                {
                    name = e.Name,
                    arguments = TryParse(e.ArgumentsRaw),
                    content = e.Content,
                    is_error = e.IsError,
                    duration_ms = e.DurationMs,
                }).ToArray(),
            };
        }

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await sink.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await sink.FlushAsync(ct).ConfigureAwait(false);
    }

    private static object? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return raw; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed class ToolEvent
    {
        public string Name { get; set; } = string.Empty;
        public string? ArgumentsRaw { get; set; }
        public string? Content { get; set; }
        public bool? IsError { get; set; }
        public long? DurationMs { get; set; }
        public bool? Completed { get; set; }
    }
}
