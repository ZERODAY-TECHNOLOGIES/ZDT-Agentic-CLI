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

    /// <summary>
    /// Per-type metadata for /agents and any UI that lists profiles. Each entry covers
    /// one supported type and includes a short blurb plus the tools that type may use.
    /// </summary>
    IReadOnlyList<SubagentTypeInfo> GetTypeInfo();
}

public sealed record SubagentRequest(
    string Description,
    string Prompt,
    string Type = "general-purpose",
    int MaxTurns = 25,
    /// <summary>
    /// Resolved model id for the subagent — usually the parent session's CURRENT model
    /// (after any /model switch), forwarded by TaskTool from <see cref="ToolContext.Model"/>.
    /// When null, the runner falls back to the parent AgentLoop's startup options. Threading
    /// it through the request rather than reading parent.Options statically lets a /model
    /// change in the REPL take effect for the next subagent dispatch, not just the parent.
    /// </summary>
    string? ParentModel = null,
    /// <summary>
    /// Optional override that takes precedence over <see cref="ParentModel"/>. Set by
    /// <c>TaskTool</c> when <c>SubagentModelResolver</c> picks a tiered model for the
    /// requested <see cref="Type"/> (e.g. <c>code-reviewer</c> → light tier). When null
    /// the runner falls through to <see cref="ParentModel"/> as before.
    /// </summary>
    string? OverrideModel = null);

public sealed record SubagentResult(
    string FinalText,
    int Turns,
    int? PromptTokens,
    int? CompletionTokens,
    /// <summary>
    /// The model id the subagent actually ran on — useful for verifying that tiered routing
    /// (litellm.subagentModels) took effect. Surfaced in TaskTool's result preamble so the
    /// caller can see e.g. <c>[subagent code-reviewer — 3 turn(s), model: glm-fast]</c>.
    /// </summary>
    string? Model = null,
    /// <summary>
    /// Outcome the subagent reported: <c>"completed"</c>, <c>"partial"</c>, or <c>"blocked"</c> — parsed
    /// from a trailing <c>STATUS:</c> line it was instructed to emit, or inferred (<c>"partial"</c> when
    /// it hit the turn cap). Null when unknown. Lets the orchestrator tell "done" from "gave up" so it
    /// doesn't blindly re-dispatch a task that was blocked. Surfaced in TaskTool's result header.
    /// </summary>
    string? Status = null);

/// <summary>
/// What /agents and the Task-tool error messages need to know about a profile.
/// AllowedTools is "*" when the profile inherits the parent's full tool set
/// (general-purpose); otherwise it lists the explicitly permitted tools.
/// </summary>
public sealed record SubagentTypeInfo(
    string Name,
    string Description,
    IReadOnlyList<string> AllowedTools);
