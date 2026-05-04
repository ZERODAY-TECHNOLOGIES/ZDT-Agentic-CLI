using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Zdtllm.Tools;

/// <summary>
/// The Agent tool — exposes a subagent-spawn primitive to the model under the same
/// name claude CLI uses (so AppSec-Automator's <c>--tools Read Glob Grep Agent</c>
/// reaches us 1:1). Lets the parent agent spin up a subagent with its own fresh
/// context, focused system prompt, and constrained tool set. Returns only the
/// subagent's final answer to the parent — intermediate tool calls stay inside the
/// subagent so the parent's context stays clean.
///
/// The implementation class is still <c>TaskTool</c> for historical reasons; only
/// the user-visible Name advertised to the model changed from "Task" → "Agent" in
/// v0.2.0 for claude-cli parity.
/// </summary>
public sealed class TaskTool : ITool
{
    /// <summary>The user-visible tool name advertised to the model. Kept as a const so
    /// the SubagentRunner's recursion guard and the REPL's /agents banner share one
    /// source of truth — renaming again only requires touching this one literal.</summary>
    public const string ToolName = "Agent";

    private readonly ISubagentRunner _runner;

    public TaskTool(ISubagentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public ToolSchema Schema { get; } = new(
        Name: ToolName,
        Description:
            "Spawn a subagent with its OWN fresh context to handle a focused sub-task. " +
            "Use when:\n" +
            "- A code review benefits from re-reading files without bias from how you just wrote them.\n" +
            "- An exploration step would clutter your main context with intermediate noise.\n" +
            "- A multi-step task needs a constrained tool set to avoid scope creep.\n\n" +
            "The subagent runs autonomously; you receive only its final answer. " +
            "subagent_type options:\n" +
            "  general-purpose — all tools available except Task itself (no recursion)\n" +
            "  code-reviewer   — Read, Glob, Grep, TodoWrite only (read-only analysis)\n" +
            "  explore         — Read, Glob, Grep, WebFetch, TodoWrite (read-only research with web)",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                description = new { type = "string", description = "Short imperative title (3-5 words) for activity logs." },
                prompt = new { type = "string", description = "The full instructions the subagent receives as its first user message." },
                subagent_type = new { type = "string", description = "general-purpose | code-reviewer | explore (default: general-purpose)." },
            },
            required = new[] { "description", "prompt" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("subagent_type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : "general-purpose";

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        var description = ReadString(args, "description");
        var prompt = ReadString(args, "prompt");
        var type = ReadString(args, "subagent_type") ?? "general-purpose";
        var maxTurns = ReadInt(args, "max_turns", 25);

        if (string.IsNullOrWhiteSpace(description))
            return ToolResult.Error("Task: missing 'description' parameter.");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("Task: missing 'prompt' parameter.");
        if (!_runner.SupportsType(type))
        {
            return ToolResult.Error(
                $"Task: unknown subagent_type '{type}'. Available: {string.Join(", ", _runner.AvailableTypes)}.");
        }

        try
        {
            var request = new SubagentRequest(description!, prompt!, type, maxTurns);
            var result = await RunWithSpinnerAsync(request, ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append("[subagent ").Append(type)
              .Append(" — ").Append(result.Turns).Append(" turn(s)");
            if (result.PromptTokens is int p) sb.Append(", ").Append(p).Append(" prompt tokens");
            if (result.CompletionTokens is int c) sb.Append(", ").Append(c).Append(" completion tokens");
            sb.AppendLine("]");
            sb.AppendLine();
            sb.Append(result.FinalText);

            return ToolResult.Success(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Task: subagent failed: {ex.Message}");
        }
    }

    /// <summary>
    /// On a real terminal, wrap the subagent call in a Spectre Status spinner so the
    /// user sees that something is happening (subagents can take 10-60s). Spectre
    /// auto-falls-back to plain output when stdout isn't a TTY (CI, piped invocations,
    /// most unit tests), so this never breaks scripted use.
    /// </summary>
    private async Task<SubagentResult> RunWithSpinnerAsync(SubagentRequest request, CancellationToken ct)
    {
        if (!AnsiConsole.Console.Profile.Capabilities.Interactive)
            return await _runner.RunAsync(request, ct).ConfigureAwait(false);

        var label = $"[#1BEACD]subagent ▸ {request.Type}[/] [#687B89]({Markup.Escape(request.Description)})[/]";
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(new Color(0x1B, 0xEA, 0xCD)))
            .StartAsync(label, async _ => await _runner.RunAsync(request, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int ReadInt(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => fallback,
        };
    }
}
