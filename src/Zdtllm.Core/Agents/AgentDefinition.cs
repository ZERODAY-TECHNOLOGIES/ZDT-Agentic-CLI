namespace Zdtllm.Core.Agents;

/// <summary>
/// A project-defined subagent — the unit the team-mode wizard creates and the
/// <see cref="SubagentRunner"/> can spawn by <c>subagent_type</c>. Persisted as
/// <c>.zdtllm/agents/&lt;name&gt;.md</c> (frontmatter + body) so it survives across sessions
/// and can be committed to the repo, mirroring claude-cli's <c>.claude/agents</c> layout.
/// </summary>
/// <param name="Name">The subagent_type used to dispatch it (lower-kebab slug, e.g. <c>db-migrator</c>).</param>
/// <param name="Description">One-line blurb shown in <c>/agents</c> and fed to the orchestrator so it
/// knows when to pick this agent.</param>
/// <param name="AllowedTools">The exact tool names this agent may use. <c>null</c> means "every tool
/// the parent has, except the Agent tool itself" (a general-purpose worker). A non-null set restricts
/// the agent to just those tools — the Agent tool is always excluded regardless (no recursive spawning).</param>
/// <param name="SystemPrompt">The focused system prompt the agent boots with, replacing the parent's.</param>
/// <param name="Model">Optional model for this agent — a tier alias (<c>light</c>/<c>medium</c>/<c>heavy</c>)
/// or a literal model id. <c>null</c> (or the sentinel <c>inherit</c>, normalised to null by the loader)
/// means "run on the orchestrator's current model".</param>
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlySet<string>? AllowedTools,
    string SystemPrompt,
    string? Model);
