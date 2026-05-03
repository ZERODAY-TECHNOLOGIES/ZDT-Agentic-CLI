namespace Zdtllm.Tools;

/// <summary>
/// Spawned by the Task tool. Implementations live in Core (where AgentLoop is)
/// — keeping the contract here means Tools doesn't have to depend on Core, so
/// the dependency direction stays Cli → Core → Tools.
/// </summary>
public interface ISubagentRunner
{
    /// <summary>Run a focused sub-task in a fresh agent context. Caller awaits the final text.</summary>
    Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct);

    /// <summary>True when the runner has a profile registered for this subagent type.</summary>
    bool SupportsType(string type);

    /// <summary>Human-readable list of supported types — surfaced in error messages.</summary>
    IReadOnlyList<string> AvailableTypes { get; }
}

public sealed record SubagentRequest(
    string Description,
    string Prompt,
    string Type = "general-purpose",
    int MaxTurns = 25);

public sealed record SubagentResult(
    string FinalText,
    int Turns,
    int? PromptTokens,
    int? CompletionTokens);
