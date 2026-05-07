using System.Collections.Immutable;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core;

/// <summary>
/// Observer hook that <see cref="AgentLoop"/> calls into during a turn. Used to power
/// alternate output formats (--output-format json / stream-json) and the verbose trace
/// (--verbose). Default-method no-ops so implementations only override what they need.
///
/// All methods are best-effort — exceptions thrown by an observer are swallowed by the
/// loop so a misbehaving sink can't take the agent down.
/// </summary>
public interface IAgentObserver
{
    /// <summary>Fired for every text-delta chunk the model streams (including in rich-console mode).</summary>
    Task OnTextDeltaAsync(string text, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired immediately before each tool dispatch starts.</summary>
    Task OnToolCallAsync(string toolName, string argumentsJson, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Fired after each tool returns (success or error) with its content + duration.</summary>
    Task OnToolResultAsync(string toolName, string content, bool isError, TimeSpan duration, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>Fired exactly once per <c>RunTurnAsync</c> call when the loop reaches a no-tool-calls turn.</summary>
    Task OnFinalAsync(string finalText, int turns, int? promptTokens, int? completionTokens, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Fired once per agent iteration with the full assistant message (text + tool_use blocks)
    /// and the per-turn token usage. Powers Anthropic-compatible <c>{"type":"assistant",...}</c>
    /// stream-json events that downstream consumers (e.g. AppSec-Automator's StreamJsonResult)
    /// scan for billed-tokens and the model that produced the turn.
    /// </summary>
    Task OnAssistantTurnAsync(
        string text,
        ImmutableArray<ToolCall> toolCalls,
        string model,
        int? inputTokens,
        int? outputTokens,
        CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Fired exactly once at the terminal end of the run — success OR error path. Carries the
    /// totals the claude-cli result event publishes: subtype (success / error_max_turns /
    /// error_during_execution), num_turns, stop_reason, summed input/output tokens. Total cost
    /// is reported separately via stream-json (always null because LiteLLM doesn't surface it).
    ///
    /// <paramref name="formatBreakdown"/> is true when the run included at least one assistant
    /// turn whose XML tool-call markup looked corrupted (close tag without matching open, stray
    /// invoke/function markers, etc.). The flag lets stream-json consumers distinguish
    /// "model deliberately ended with text" from "model emitted tool calls but the wire layer
    /// truncated the open tag" without pattern-matching on result.text.
    /// </summary>
    Task OnResultAsync(
        string subtype,
        bool isError,
        int numTurns,
        string? stopReason,
        string? resultText,
        int totalInputTokens,
        int totalOutputTokens,
        CancellationToken ct,
        bool formatBreakdown = false) => Task.CompletedTask;

    /// <summary>
    /// Fired immediately when AgentLoop detects an XML-markup format breakdown mid-turn (so a
    /// downstream consumer can react before the turn finishes). The terminal result event also
    /// carries <c>formatBreakdown=true</c>; this hook is just the early-warning channel for
    /// long-running sessions where waiting for the result event would mean tens of seconds of
    /// wasted compute on a model that's clearly not producing useful tool calls.
    /// </summary>
    Task OnFormatBreakdownAsync(string details, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Fired once when the upstream proxy returned HTTP 429 and our retries have been
    /// exhausted (or we know the rate-limit window won't open in time). Powers Anthropic-
    /// compatible <c>{"type":"rate_limit_event","rate_limit_info":{...}}</c> events that
    /// downstream consumers (AppSec-Automator's DetectsRateLimit) parse into a structured
    /// "try again at X" signal. <paramref name="resetsAtUnix"/> may be null when the upstream
    /// gave us no Retry-After / x-ratelimit-reset hint.
    /// </summary>
    Task OnRateLimitedAsync(
        string status,
        long? resetsAtUnix,
        CancellationToken ct) => Task.CompletedTask;
}
