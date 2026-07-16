namespace Zdtllm.Core.Workflows;

/// <summary>
/// A declarative multi-agent workflow: an ordered list of phases, each dispatching one or more
/// subagents. Phases run sequentially; a phase with <see cref="WorkflowPhase.ForEach"/> fans a
/// subagent out over the items of an input list. Later phases can reference earlier ones through
/// <c>{{PhaseTitle.results}}</c> templating. Deterministic — the orchestration is fixed by the
/// file, only the subagents' text is model-driven, so it stays model-agnostic.
/// </summary>
public sealed record WorkflowDefinition(
    string Name,
    string? Description,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<WorkflowPhase> Phases);

/// <summary>
/// One phase of a workflow. Without <see cref="ForEach"/> it's a single subagent run; with it, the
/// named input is split into items and one subagent runs per item (in parallel when
/// <see cref="Parallel"/> is true). <see cref="Prompt"/> is templated per run.
/// </summary>
public sealed record WorkflowPhase(
    string Title,
    string Agent,
    string Prompt,
    string? ForEach,
    bool Parallel,
    int MaxTurns);

/// <summary>The outputs a single phase produced (one entry per subagent it ran).</summary>
public sealed record WorkflowPhaseResult(string Title, IReadOnlyList<string> Outputs);

/// <summary>The result of running a whole workflow: per-phase outputs plus the final phase's text.</summary>
public sealed record WorkflowResult(
    string Name,
    IReadOnlyList<WorkflowPhaseResult> Phases,
    string FinalOutput);

/// <summary>A one-line summary of a workflow file, for listing.</summary>
public sealed record WorkflowSummary(string Name, string? Description, int PhaseCount);

/// <summary>Raised when a workflow file is missing or malformed.</summary>
public sealed class WorkflowException : Exception
{
    public WorkflowException(string message, Exception? inner = null) : base(message, inner) { }
}
