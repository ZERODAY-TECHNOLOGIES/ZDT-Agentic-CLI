namespace Zdtllm.Tools;

/// <summary>
/// Shared, mutable plan-mode flag. In plan mode the agent researches and drafts a plan but is
/// blocked from mutating the workspace (Write / Edit / NotebookEdit / Bash), exactly like
/// claude-cli's plan mode. One instance is shared between the AgentLoop (which gates tools and
/// grounds the model), the REPL (the <c>/plan</c> toggle + status), and
/// <see cref="ExitPlanModeTool"/> (which flips it off once the user approves a plan).
/// </summary>
public interface IPlanModeSwitch
{
    bool InPlanMode { get; }

    /// <summary>Enter plan mode (read-only, drafting a plan).</summary>
    void Enter();

    /// <summary>Leave plan mode — the plan was approved, mutations are allowed again.</summary>
    void Approve();
}

/// <inheritdoc cref="IPlanModeSwitch"/>
public sealed class PlanModeState : IPlanModeSwitch
{
    private volatile bool _active;

    public PlanModeState(bool active = false) => _active = active;

    public bool InPlanMode => _active;
    public void Enter() => _active = true;
    public void Approve() => _active = false;

    /// <summary>Tools the agent may not use while in plan mode (they mutate the workspace).</summary>
    public static readonly IReadOnlySet<string> BlockedTools =
        new HashSet<string>(StringComparer.Ordinal) { "Write", "Edit", "NotebookEdit", "Bash" };

    /// <summary>Appended to each user prompt while in plan mode so any model stays grounded.</summary>
    public const string Reminder =
        "[plan mode is ON] Do NOT modify files or run commands — Write, Edit, NotebookEdit and " +
        "Bash are blocked. Investigate with read-only tools (Read, Glob, Grep, WebFetch, WebSearch, " +
        "Agent) and design a concrete, step-by-step plan. When the plan is ready, call the " +
        "ExitPlanMode tool with it to present it for the user's approval — do not start " +
        "implementing until they approve.";
}
