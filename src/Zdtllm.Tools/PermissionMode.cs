namespace Zdtllm.Tools;

/// <summary>
/// The interaction mode that governs how tool permissions are resolved, matching claude-cli's
/// Shift+Tab cycle. Orthogonal to the per-tool allow/ask/deny RULES: a mode only decides what
/// happens to a call that would otherwise ASK.
/// </summary>
public enum PermissionMode
{
    /// <summary>Ask the human for every permission-gated call (the historic default).</summary>
    Default,

    /// <summary>Auto-allow file edits (Edit/Write/NotebookEdit); everything else still asks. The
    /// mode iterative coding leans on most.</summary>
    AcceptEdits,

    /// <summary>Read-only: mutating tools are blocked, the model drafts a plan for approval.</summary>
    Plan,

    /// <summary>Auto-allow everything (equivalent to --dangerously-skip-permissions), EXCEPT ops the
    /// hardcoded deny-floor flags as dangerous, which always require an interactive confirm.</summary>
    Bypass,
}

/// <summary>
/// Runtime-switchable permission mode, shared between the AgentLoop (which gates tools), the REPL
/// and TUI (the Shift+Tab / <c>/mode</c> toggle + footer), and <see cref="ExitPlanModeTool"/>. It
/// implements <see cref="IPlanModeSwitch"/> so the existing plan-mode plumbing keeps working —
/// <c>Plan</c> is now just one point on the mode cycle.
/// </summary>
public interface IPermissionModeSwitch : IPlanModeSwitch
{
    PermissionMode Mode { get; }
    void SetMode(PermissionMode mode);
    /// <summary>Advance the SAFE cycle Default → AcceptEdits → Plan → Default (Bypass is never
    /// reached by cycling — it must be chosen explicitly). Returns the new mode.</summary>
    PermissionMode Cycle();
}

/// <inheritdoc cref="IPermissionModeSwitch"/>
public sealed class PermissionModeState : IPermissionModeSwitch
{
    private volatile int _mode;

    public PermissionModeState(PermissionMode mode = PermissionMode.Default) => _mode = (int)mode;

    public PermissionMode Mode => (PermissionMode)_mode;
    public void SetMode(PermissionMode mode) => _mode = (int)mode;

    // IPlanModeSwitch bridge: plan mode is just Mode == Plan.
    public bool InPlanMode => Mode == PermissionMode.Plan;
    public void Enter() => _mode = (int)PermissionMode.Plan;
    public void Approve() { if (Mode == PermissionMode.Plan) _mode = (int)PermissionMode.Default; }

    public PermissionMode Cycle()
    {
        var next = Mode switch
        {
            PermissionMode.Default => PermissionMode.AcceptEdits,
            PermissionMode.AcceptEdits => PermissionMode.Plan,
            PermissionMode.Plan => PermissionMode.Default,
            PermissionMode.Bypass => PermissionMode.Default, // cycling leaves bypass for safety
            _ => PermissionMode.Default,
        };
        _mode = (int)next;
        return next;
    }

    /// <summary>Human-readable label for the footer / status line.</summary>
    public static string Label(PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => "accept-edits",
        PermissionMode.Plan => "plan",
        PermissionMode.Bypass => "bypass",
        _ => "default",
    };

    /// <summary>Tools auto-allowed under <see cref="PermissionMode.AcceptEdits"/>.</summary>
    public static readonly IReadOnlySet<string> EditTools =
        new HashSet<string>(StringComparer.Ordinal) { "Edit", "Write", "NotebookEdit" };
}
