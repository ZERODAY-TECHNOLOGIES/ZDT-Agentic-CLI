using System.Text.Json;

namespace Zdtllm.Tools;

/// <summary>
/// The model calls this once it has a plan ready, to present it and ask the user to approve
/// before any changes are made — the counterpart to claude-cli's ExitPlanMode. In interactive
/// mode the plan is shown and the user picks "Approve" / "Keep planning" with the arrow keys; on
/// approval the shared <see cref="IPlanModeSwitch"/> is flipped so mutating tools unlock on the
/// next turn. Only registered in interactive mode + while plan mode is reachable.
/// </summary>
public sealed class ExitPlanModeTool : ITool
{
    public const string ToolName = "ExitPlanMode";

    private readonly IPlanModeSwitch _plan;
    private readonly IInteractivePrompter _prompter;

    public ExitPlanModeTool(IPlanModeSwitch plan, IInteractivePrompter prompter)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(prompter);
        _plan = plan;
        _prompter = prompter;
    }

    public ToolSchema Schema { get; } = new(
        Name: ToolName,
        Description:
            "Present your finished plan to the user and ask them to approve it before you make any " +
            "changes. Call this ONLY when you are in plan mode and have a concrete, ordered plan. " +
            "On approval, plan mode ends and you may edit files / run commands on the next turn; if " +
            "the user declines, stay in plan mode and refine the plan.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                plan = new
                {
                    type = "string",
                    description = "The plan to present, as Markdown. Concrete and ordered — the steps you intend to take.",
                },
            },
            required = new[] { "plan" },
        }));

    public bool CanRunInParallel => false;
    public bool IsInteractive => true;
    public ITool CloneForSubagent() => new ExitPlanModeTool(_plan, UnavailablePrompter.Instance);

    public string? GetSpecifierForPermissions(JsonElement args) => null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!_plan.InPlanMode)
            return ToolResult.Error(
                "ExitPlanMode: not currently in plan mode — nothing to approve. Just proceed normally.");

        var plan = args.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(plan))
            return ToolResult.Error("ExitPlanMode: missing 'plan' text to present.");

        if (!_prompter.IsAvailable)
            return ToolResult.Error(
                "ExitPlanMode is unavailable here (no interactive terminal). Present the plan as your " +
                "final answer instead.");

        var question = "Approve this plan and proceed?\n\n" + plan!.Trim();
        var options = new[]
        {
            new PromptChoice("Approve — proceed with changes", "Exit plan mode and start implementing"),
            new PromptChoice("Keep planning", "Stay in plan mode and refine"),
        };

        IReadOnlyList<string> chosen;
        try
        {
            chosen = await _prompter.SelectAsync(question, "Plan", options, multiSelect: false, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"ExitPlanMode: prompting failed: {ex.Message}");
        }

        var approved = chosen.Count > 0 && chosen[0].StartsWith("Approve", StringComparison.Ordinal);
        if (approved)
        {
            _plan.Approve();
            return ToolResult.Success(
                "[plan approved] Plan mode is now OFF. You may modify files and run commands from the " +
                "next turn on. Proceed with the plan you presented.");
        }

        return ToolResult.Success(
            "[plan not approved] The user wants to keep planning. Stay in plan mode: refine the plan " +
            "based on any feedback and do not make changes.");
    }
}
