using System.Collections.Immutable;
using System.Text.Json;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Observers;

/// <summary>
/// Anthropic-compatible NDJSON sink for <c>--output-format stream-json</c>. Emits one event
/// per assistant iteration (text + tool_use blocks + per-turn usage) plus exactly one
/// terminal <c>{"type":"result",...}</c> event with summed totals. The shape matches what
/// <c>claude --output-format stream-json</c> produces so consumers like AppSec-Automator
/// (StreamJsonResult.php / DetectsRateLimit.php) parse zdt's output without modification.
///
/// Events:
///   <code>{"type":"assistant","message":{"role":"assistant","model":"...","content":[
///       {"type":"text","text":"..."},
///       {"type":"tool_use","id":"...","name":"...","input":{...}}],
///       "usage":{"input_tokens":N,"output_tokens":N}}}</code>
///   <code>{"type":"result","subtype":"success|error_max_turns|error_during_execution",
///       "is_error":bool,"num_turns":N,"stop_reason":"end_turn|max_turns|...",
///       "total_cost_usd":null,"result":"final text",
///       "input_tokens":N,"output_tokens":N,
///       "usage":{"input_tokens":N,"output_tokens":N,
///                "cache_creation_input_tokens":0,"cache_read_input_tokens":0}}</code>
/// The flat <c>input_tokens</c>/<c>output_tokens</c> at the top level are kept for back-compat
/// with consumers built against earlier zdt builds; the nested <c>usage</c> object mirrors
/// the claude-cli shape so consumers that walk <c>$event['usage']['input_tokens']</c> (the
/// path the official @anthropic-ai/claude-code SDK reads) parse zdt's output unmodified.
///
/// Per-delta <c>text_delta</c> / <c>tool_call</c> / <c>tool_result</c> events from earlier zdt
/// builds are intentionally gone — claude doesn't emit them either, and consumers built
/// against claude expect the per-turn <c>assistant</c> shape. Use <c>--verbose</c> if you
/// want a human-readable trace of tool calls on stderr.
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

    public async Task OnAssistantTurnAsync(
        string text,
        ImmutableArray<ToolCall> toolCalls,
        string model,
        int? inputTokens,
        int? outputTokens,
        CancellationToken ct)
    {
        var content = new List<object>();
        if (!string.IsNullOrEmpty(text))
            content.Add(new { type = "text", text });
        if (!toolCalls.IsDefaultOrEmpty)
        {
            foreach (var call in toolCalls)
            {
                // Embed the args as a parsed JSON object when possible — claude does that and
                // consumers reading "input" expect a structured value, not a stringified one.
                object input = TryParseJson(call.Arguments) ?? (object)call.Arguments;
                content.Add(new
                {
                    type = "tool_use",
                    id = call.Id,
                    name = call.FunctionName,
                    input,
                });
            }
        }

        var message = new
        {
            role = "assistant",
            model,
            content,
            usage = new
            {
                input_tokens = inputTokens ?? 0,
                output_tokens = outputTokens ?? 0,
            },
        };

        await EmitAsync(new { type = "assistant", message }, ct).ConfigureAwait(false);
    }

    public async Task OnRateLimitedAsync(string status, long? resetsAtUnix, CancellationToken ct) =>
        await EmitAsync(new
        {
            type = "rate_limit_event",
            rate_limit_info = new
            {
                status,
                resetsAt = resetsAtUnix,
            },
        }, ct).ConfigureAwait(false);

    public async Task OnResultAsync(
        string subtype,
        bool isError,
        int numTurns,
        string? stopReason,
        string? resultText,
        int totalInputTokens,
        int totalOutputTokens,
        CancellationToken ct,
        bool formatBreakdown = false,
        int toolErrorCount = 0) =>
        await EmitAsync(new
        {
            type = "result",
            subtype,
            is_error = isError,
            num_turns = numTurns,
            stop_reason = stopReason,
            // LiteLLM doesn't surface a deterministic per-call cost — we leave this null and
            // let the consumer either ignore the field or compute its own from input/output.
            total_cost_usd = (double?)null,
            result = resultText,
            // Flat fields kept for back-compat with consumers that read tokens off the result
            // root (every released zdt build emitted them this way).
            input_tokens = totalInputTokens,
            output_tokens = totalOutputTokens,
            // Distinguishes "model deliberately ended with text only" from "model emitted XML
            // tool-call markup but an upstream layer corrupted the open tag, so the parser saw
            // no calls and we ended the turn as if there were none". The flag lets consumers
            // skip pattern-matching on result.text to reach the same conclusion.
            format_breakdown = formatBreakdown,
            // Tool-error telemetry — non-breaking addition. subtype stays "success" when the
            // model itself ended cleanly so existing consumers that branch on subtype keep
            // working; consumers that need to distinguish "ran cleanly" from "every tool call
            // failed" gate on these fields instead. had_tool_errors is the boolean form for
            // consumers that don't want to compare ints; tool_error_count is the scalar.
            had_tool_errors = toolErrorCount > 0,
            tool_error_count = toolErrorCount,
            // Nested usage object mirrors the claude-cli shape; cache_* are always 0 because
            // LiteLLM /v1/chat/completions doesn't expose Anthropic-style prompt-cache totals
            // on non-Anthropic backends. Emitting them as 0 (instead of omitting) lets a
            // strict consumer that expects four keys still parse without a missing-field branch.
            usage = new
            {
                input_tokens = totalInputTokens,
                output_tokens = totalOutputTokens,
                cache_creation_input_tokens = 0,
                cache_read_input_tokens = 0,
            },
        }, ct).ConfigureAwait(false);

    public async Task OnFormatBreakdownAsync(string details, CancellationToken ct) =>
        await EmitAsync(new
        {
            type = "warning",
            subtype = "format_breakdown",
            details,
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

    private static object? TryParseJson(string raw)
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
