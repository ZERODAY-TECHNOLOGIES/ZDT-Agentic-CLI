using System.Text;
using System.Text.Json;

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
    private readonly Func<string, string?, string?>? _modelResolver;

    public TaskTool(ISubagentRunner runner)
        : this(runner, modelResolver: null)
    {
    }

    /// <summary>
    /// Construct with a tiered model resolver. The delegate is called per subagent dispatch
    /// with <c>(subagent_type, parentModel)</c> and returns either the override model id
    /// for that type or null to inherit the parent. Wired up in CLI from
    /// <c>SubagentModelResolver.Resolve(...)</c> so <c>litellm.subagentModels</c> takes effect.
    /// </summary>
    public TaskTool(ISubagentRunner runner, Func<string, string?, string?>? modelResolver)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _modelResolver = modelResolver;
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
            return ToolResult.Error("Agent: missing 'description' parameter.");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("Agent: missing 'prompt' parameter.");
        if (!_runner.SupportsType(type))
        {
            return ToolResult.Error(
                $"Agent: unknown subagent_type '{type}'. Available: {string.Join(", ", _runner.AvailableTypes)}.");
        }

        try
        {
            // Forward the parent session's CURRENT model so a /model switch in the REPL
            // reaches the subagent. ParentModel is null when the caller didn't set ctx.Model
            // (e.g. unit tests building a ToolContext directly) — runner falls back to its
            // own AgentLoop options in that case.
            //
            // OverrideModel comes from the tiered resolver (SubagentModelResolver) which the
            // CLI wires up at startup. When the user mapped this subagent_type to a different
            // tier in litellm.subagentModels, the override wins over ParentModel. When no
            // mapping exists, the resolver returns null and the runner falls through to
            // ParentModel — preserving the pre-tier behaviour.
            var overrideModel = _modelResolver?.Invoke(type, ctx.Model);
            var request = new SubagentRequest(
                Description: description!,
                Prompt: prompt!,
                Type: type,
                MaxTurns: maxTurns,
                ParentModel: ctx.Model,
                OverrideModel: overrideModel);

            // Deliberately NOT wrapping the call in AnsiConsole.Status(): when several Agent
            // tool calls dispatch in parallel (the parent's parallel-batch path), each parallel
            // RunWithSpinnerAsync would race on Spectre's single AnsiConsole exclusivity lock
            // and throw "Trying to run one or more interactive functions concurrently". The
            // parent agent already prints a "[Agent] {description}" status line per call before
            // dispatch (see AgentLoop.FormatStatusLine), and for sequential single calls the
            // parent's ExecuteToolWithSpinnerAsync wraps execution in its own Status() — so we
            // already have the visual feedback without the nested-Status conflict.
            var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append("[subagent ").Append(type)
              .Append(" — ").Append(result.Turns).Append(" turn(s)");
            if (!string.IsNullOrEmpty(result.Model))
                sb.Append(", model: ").Append(result.Model);
            if (result.PromptTokens is int p) sb.Append(", ").Append(p).Append(" prompt tokens");
            if (result.CompletionTokens is int c) sb.Append(", ").Append(c).Append(" completion tokens");
            sb.AppendLine("]");
            sb.AppendLine();
            sb.Append(result.FinalText);

            return ToolResult.Success(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Agent: subagent failed: {ex.Message}");
        }
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
