using System.Text;

namespace Zdtllm.Core.Agents;

/// <summary>
/// Shared, mutable team-mode flag. In team mode the big model becomes a pure ORCHESTRATOR: it may
/// research (Read/Grep/Glob) and delegate, but the workspace-mutating tools (Write/Edit/Bash/
/// NotebookEdit) are hidden from it and hard-blocked so every implementation step must be handed to
/// a subagent via the Agent tool. Deliberately sticky — once entered, only <c>/end-team</c> leaves —
/// so a long session can't quietly drift back to the model doing the work itself. Modelled on
/// <c>PlanModeState</c>; one instance is shared between the AgentLoop (gates tools + grounds the
/// model), the REPL (the <c>/team</c> / <c>/end-team</c> toggle + status), and the input TUI (status row).
/// </summary>
public interface ITeamModeSwitch
{
    /// <summary>True while the orchestrator-only team mode is active.</summary>
    bool InTeamMode { get; }

    /// <summary>Enter team mode (orchestrator-only; implementation tools blocked).</summary>
    void Enter();

    /// <summary>Leave team mode — the model may implement directly again. Only <c>/end-team</c> calls this.</summary>
    void End();
}

/// <inheritdoc cref="ITeamModeSwitch"/>
public sealed class TeamModeState : ITeamModeSwitch
{
    private volatile bool _active;

    public TeamModeState(bool active = false) => _active = active;

    public bool InTeamMode => _active;
    public void Enter() => _active = true;
    public void End() => _active = false;

    /// <summary>
    /// Tools the orchestrator may NOT use while in team mode — they change the workspace, which is a
    /// subagent's job. Kept identical to plan mode's blocked set so "delegate, don't do" is enforced
    /// on exactly the mutating surface. The Agent tool and read-only tools stay available.
    /// </summary>
    public static readonly IReadOnlySet<string> BlockedTools =
        new HashSet<string>(StringComparer.Ordinal) { "Write", "Edit", "NotebookEdit", "Bash" };

    /// <summary>
    /// Grounding text appended to every user turn while team mode is on, so any model — however far
    /// the context has drifted — keeps orchestrating instead of implementing. The hard guarantee is
    /// the tool-dispatch block in AgentLoop; this keeps the model cooperative and, crucially, tells it
    /// which subagents exist right now (the roster is dynamic — the wizard can add agents mid-session).
    /// </summary>
    public static string BuildReminder(IReadOnlyList<AgentDefinition> projectAgents)
    {
        ArgumentNullException.ThrowIfNull(projectAgents);

        var sb = new StringBuilder();
        sb.Append(
            "[TEAM MODE ON — you are the ORCHESTRATOR] Do NOT implement changes yourself: Write, Edit, " +
            "NotebookEdit and Bash are blocked for you. Break the request into focused sub-tasks and " +
            "dispatch EACH one to a subagent with the Agent tool (pick the subagent_type that fits). " +
            "Dispatch independent sub-tasks in parallel. Use Read/Grep/Glob only to understand the code " +
            "well enough to write a precise prompt for each subagent. When subagents report back, verify " +
            "and integrate their results, then summarise for the user. Each subagent ends with a STATUS " +
            "(completed / partial / blocked); if one returns blocked or partial, act on the stated blocker " +
            "— do NOT re-dispatch the same task (identical re-dispatches are blocked): change the approach " +
            "or split it smaller. If a needed specialist is missing, say so — the user can add one with /team.");

        if (projectAgents.Count > 0)
        {
            sb.Append("\n\nProject subagents available for this task:");
            foreach (var a in projectAgents)
                sb.Append("\n  - ").Append(a.Name).Append(" — ").Append(a.Description);
        }

        // List only the built-ins a project agent hasn't redefined, so the advertised roster never
        // contradicts what the name actually dispatches to (dispatch is registry-first).
        var shadowed = projectAgents.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var builtins = new (string Name, string Blurb)[]
        {
            ("general-purpose", "all tools"),
            ("code-reviewer", "read-only review"),
            ("explore", "read-only research"),
        }.Where(b => !shadowed.Contains(b.Name)).ToList();
        if (builtins.Count > 0)
            sb.Append("\n\nAlso available: ")
              .Append(string.Join(", ", builtins.Select(b => $"{b.Name} ({b.Blurb})")))
              .Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// Forced-dispatch nudge. The schema filter + block only stop the orchestrator from MUTATING;
    /// they don't stop it from quietly answering a task with read-only tools and never delegating —
    /// which is exactly the drift on follow-up turns (the model settles into "just answer"). When a
    /// team-mode turn does non-delegating work but dispatches no subagent, AgentLoop appends this as a
    /// user turn and re-runs once, so the orchestrator has to actually hand the work off. The escape
    /// clause keeps a genuine read-only question from being trapped in a delegate-forever loop.
    /// </summary>
    public const string ForcedDispatchNudge =
        "[TEAM MODE] You finished this task without dispatching a subagent. You are the ORCHESTRATOR: " +
        "any task that creates, modifies, runs, or otherwise changes code MUST be carried out by a " +
        "subagent via the Agent tool — do not describe a change, or report it as done, unless a " +
        "subagent actually performed it. Break the task into focused sub-tasks and dispatch each to a " +
        "fitting subagent_type now (independent ones in parallel). ONLY if this was a pure read-only " +
        "question that needs no code changes at all, restate your answer as-is and it will be accepted.";

    /// <summary>The tool-result message handed back when the orchestrator tries a blocked tool anyway
    /// (a backstop for XML mode / stale context where the tool wasn't filtered out of the schema).</summary>
    public static string BlockedMessage(string toolName) =>
        $"[blocked: team mode is ON] `{toolName}` changes the workspace and must be delegated. You are " +
        $"the orchestrator — spawn a subagent with the Agent tool (choose a fitting subagent_type) and " +
        $"have IT perform this operation. Do not call {toolName} yourself; use /end-team first if you " +
        "genuinely need to implement directly.";
}
