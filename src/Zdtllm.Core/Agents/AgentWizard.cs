using System.Text;
using System.Text.RegularExpressions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Agents;

/// <summary>
/// The interactive "define a project subagent" flow behind <c>/team</c>. Built entirely on
/// <see cref="IInteractivePrompter"/> — the same arrow-key/free-text primitive that backs
/// AskUserQuestion — so it works identically under the bottom-input TUI and the classic REPL (the
/// two-driver rule) without touching raw stdin. Each run defines ONE agent: it collects a name, a
/// one-line role, a tool set, a model, and a system prompt, then persists
/// <c>.zdtllm/agents/&lt;name&gt;.md</c> and registers the definition live so the orchestrator can
/// dispatch it immediately. The caller (the REPL) owns the "define at least one / add another" loop.
/// </summary>
public sealed class AgentWizard
{
    private static readonly Regex ValidName = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>The tools offered in the multi-select, in a sensible order. Names must match the real
    /// tool schema names so a restricted registry actually finds them.</summary>
    private static readonly (string Tool, string Blurb)[] SelectableTools =
    {
        ("Read", "read files"),
        ("Grep", "search file contents"),
        ("Glob", "find files by pattern"),
        ("Edit", "modify existing files"),
        ("Write", "create / overwrite files"),
        ("Bash", "run shell commands"),
        ("NotebookEdit", "edit Jupyter notebooks"),
        ("TodoWrite", "track a task list"),
        ("WebFetch", "fetch a URL"),
        ("WebSearch", "search the web"),
    };

    internal const string AllToolsLabel = "★ All tools (general-purpose worker)";
    internal const string GeneratePromptLabel = "Generate a role prompt for me (recommended)";
    internal const string WritePromptLabel = "Write the system prompt myself";
    internal const string InheritModelLabel = "Inherit the orchestrator's model (recommended)";

    private readonly IInteractivePrompter _prompter;
    private readonly TeamAgentRegistry _registry;
    private readonly string _cwd;
    private readonly TextWriter _output;

    public AgentWizard(IInteractivePrompter prompter, TeamAgentRegistry registry, string cwd, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        ArgumentNullException.ThrowIfNull(output);
        _prompter = prompter;
        _registry = registry;
        _cwd = cwd;
        _output = output;
    }

    /// <summary>
    /// Run the define-one-agent flow. Returns the created definition (also written to disk and added
    /// to the registry), or null if no interactive prompter is available or the user gave no usable
    /// name. Never throws for user cancellation — a cancelled prompt bubbles as OperationCanceledException
    /// which the caller's turn-cancellation path already handles.
    /// </summary>
    public async Task<AgentDefinition?> RunAsync(CancellationToken ct = default)
    {
        if (!_prompter.IsAvailable)
        {
            await _output.WriteLineAsync(
                "  Defining a subagent needs an interactive terminal (not available in -p / redirected runs).")
                .ConfigureAwait(false);
            return null;
        }

        var name = await PromptNameAsync(ct).ConfigureAwait(false);
        if (name is null) return null; // no usable name → abort this definition

        var description = await PromptTextAsync(
            $"One-line role for '{name}' (what does it specialise in?)", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(description))
            description = $"project subagent ({name})";

        var tools = await PromptToolsAsync(name, ct).ConfigureAwait(false);
        var model = await PromptModelAsync(name, ct).ConfigureAwait(false);
        var systemPrompt = await PromptSystemPromptAsync(name, description, tools, ct).ConfigureAwait(false);

        var def = new AgentDefinition(name, description.Trim(), tools, systemPrompt, model);

        var path = await PersistAsync(def, ct).ConfigureAwait(false);
        _registry.Add(def);

        await _output.WriteLineAsync(
            $"  ✓ subagent '{def.Name}' ready — {DescribeToolPolicy(tools)}, model: {model ?? "inherit"}")
            .ConfigureAwait(false);
        if (path is not null)
            await _output.WriteLineAsync($"    saved to {path} (edit it by hand to refine)").ConfigureAwait(false);
        return def;
    }

    private async Task<string?> PromptNameAsync(CancellationToken ct)
    {
        var raw = await PromptTextAsync(
            "Name for this subagent (a short slug, e.g. db-migrator)", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var slug = Slugify(raw);
        if (!ValidName.IsMatch(slug)) return null;

        if (_registry.Contains(slug))
            await _output.WriteLineAsync($"  (redefining existing subagent '{slug}')").ConfigureAwait(false);
        return slug;
    }

    private async Task<IReadOnlySet<string>?> PromptToolsAsync(string name, CancellationToken ct)
    {
        var options = new List<PromptChoice> { new(AllToolsLabel, "no restriction — every tool the orchestrator has") };
        options.AddRange(SelectableTools.Select(t => new PromptChoice(t.Tool, t.Blurb)));

        var chosen = await _prompter.SelectAsync(
            $"Which tools may '{name}' use? (space to toggle, enter to confirm)",
            "Tools", options, multiSelect: true, allowFreeText: false, ct).ConfigureAwait(false);

        // "All tools" selected, or nothing selected → the general-purpose profile (null = no restriction).
        if (chosen.Count == 0 || chosen.Contains(AllToolsLabel)) return null;

        var set = new HashSet<string>(chosen.Where(c => c != AllToolsLabel), StringComparer.Ordinal);
        return set.Count == 0 ? null : set;
    }

    private async Task<string?> PromptModelAsync(string name, CancellationToken ct)
    {
        var options = new[]
        {
            new PromptChoice(InheritModelLabel, "same model the orchestrator runs on"),
            new PromptChoice("light", "the light tier from litellm.models"),
            new PromptChoice("medium", "the medium tier"),
            new PromptChoice("heavy", "the heavy tier"),
        };

        var chosen = await _prompter.SelectAsync(
            $"Which model should '{name}' run on?",
            "Model", options, multiSelect: false, allowFreeText: true, ct).ConfigureAwait(false);

        var pick = chosen.Count > 0 ? chosen[0] : InheritModelLabel;
        if (pick == InheritModelLabel || pick == "(no answer)") return null;
        return pick.Trim();
    }

    private async Task<string> PromptSystemPromptAsync(
        string name, string description, IReadOnlySet<string>? tools, CancellationToken ct)
    {
        var options = new[]
        {
            new PromptChoice(GeneratePromptLabel, "a solid default built from the role above"),
            new PromptChoice(WritePromptLabel, "type your own now (you can also edit the file later)"),
        };

        var chosen = await _prompter.SelectAsync(
            $"System prompt for '{name}'?", "Prompt", options,
            multiSelect: false, allowFreeText: false, ct).ConfigureAwait(false);

        var pick = chosen.Count > 0 ? chosen[0] : GeneratePromptLabel;
        if (pick == WritePromptLabel)
        {
            var custom = await PromptTextAsync($"System prompt for '{name}'", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        }
        return GenerateSystemPrompt(name, description, tools);
    }

    /// <summary>A focused, delegation-aware default prompt — tuned so a mid-size model stays on task
    /// and returns something the orchestrator can integrate without a follow-up round-trip.</summary>
    internal static string GenerateSystemPrompt(string name, string description, IReadOnlySet<string>? tools)
    {
        var toolLine = tools is { Count: > 0 }
            ? "Tools available to you: " + string.Join(", ", tools.OrderBy(t => t, StringComparer.Ordinal)) + "."
            : "You have the full tool set the orchestrator has (except spawning further subagents).";

        var sb = new StringBuilder();
        sb.Append("You are the '").Append(name).Append("' subagent, dispatched by an orchestrator to: ")
          .Append(description.TrimEnd('.')).Append(".\n\n");
        sb.Append("You run in your OWN fresh context and cannot see the parent conversation — the prompt " +
                  "you receive is the whole brief. ").Append(toolLine).Append("\n\n");
        sb.Append("Work autonomously and decisively: do not ask the orchestrator questions mid-task — make " +
                  "the reasonable choice and note it. Stay strictly within your brief; if you discover work " +
                  "outside it, report it rather than doing it. When finished, return a concise report: what " +
                  "you did (cite file:line for edits), how you verified it, and anything the orchestrator " +
                  "needs to know to integrate your work.");
        return sb.ToString();
    }

    private async Task<string?> PersistAsync(AgentDefinition def, CancellationToken ct)
    {
        try
        {
            var dir = AgentDefinitionLoader.ProjectRoot(_cwd);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, def.Name + ".md");
            await File.WriteAllTextAsync(path, AgentDefinitionLoader.ToMarkdown(def), ct).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex)
        {
            // Persistence is best-effort — the agent is still usable this session via the registry.
            await _output.WriteLineAsync($"  (could not write the agent file: {ex.Message}; it will work this session only)")
                .ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Collect one line of free text through the shared prompter (works in both drivers).</summary>
    private async Task<string?> PromptTextAsync(string question, CancellationToken ct)
    {
        var answer = await _prompter.SelectAsync(
            question, null, Array.Empty<PromptChoice>(), multiSelect: false, allowFreeText: true, ct)
            .ConfigureAwait(false);
        var text = answer.Count > 0 ? answer[0] : null;
        return string.IsNullOrWhiteSpace(text) || text == "(no answer)" ? null : text;
    }

    private static string DescribeToolPolicy(IReadOnlySet<string>? tools) =>
        tools is { Count: > 0 }
            ? "tools: " + string.Join(", ", tools.OrderBy(t => t, StringComparer.Ordinal))
            : "all tools";

    /// <summary>Coerce arbitrary user text into a valid lower-kebab slug (spaces/underscores → '-',
    /// drop anything else, collapse repeats). Returns "" when nothing usable survives.</summary>
    internal static string Slugify(string raw)
    {
        var lowered = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch is ' ' or '_' or '-' or '.') { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); }
        }
        return sb.ToString().Trim('-');
    }
}
