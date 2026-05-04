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
}
