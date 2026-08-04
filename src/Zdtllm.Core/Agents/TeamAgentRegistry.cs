using System.Collections.Immutable;

namespace Zdtllm.Core.Agents;

/// <summary>
/// The live set of project-defined subagents for this session. Seeded at startup from
/// <see cref="AgentDefinitionLoader"/> (disk) and mutated at runtime by the team-mode wizard, so an
/// agent the user defines mid-session is immediately dispatchable by the orchestrator WITHOUT a
/// restart. Shared by reference between the wizard (writes) and the SubagentRunner / AgentLoop
/// (reads), so all three see one roster. Thread-safe: subagents dispatch on pool threads while the
/// REPL thread may be adding a new definition.
/// </summary>
public sealed class TeamAgentRegistry
{
    private readonly object _gate = new();
    // Ordinal, case-sensitive keys — subagent_type slugs are always lower-kebab (loader-validated).
    // volatile so a definition the REPL thread swaps in via Add() is immediately visible to subagent
    // dispatches on pool threads (the whole "usable without a restart" guarantee), even on ARM64.
    private volatile ImmutableDictionary<string, AgentDefinition> _byName =
        ImmutableDictionary.Create<string, AgentDefinition>(StringComparer.Ordinal);

    public TeamAgentRegistry() { }

    public TeamAgentRegistry(IEnumerable<AgentDefinition> initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        foreach (var def in initial) Add(def);
    }

    /// <summary>Add or replace a definition (same name overwrites — re-defining an agent updates it).</summary>
    public void Add(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate) _byName = _byName.SetItem(definition.Name, definition);
    }

    public bool TryGet(string name, out AgentDefinition definition)
    {
        // Volatile read of an immutable snapshot (the field is volatile) — no lock needed on the
        // common dispatch path.
        return _byName.TryGetValue(name, out definition!);
    }

    public bool Contains(string name) => _byName.ContainsKey(name);

    public int Count => _byName.Count;

    /// <summary>All definitions, ordered by name for stable display and reminder text.</summary>
    public IReadOnlyList<AgentDefinition> All =>
        _byName.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();

    /// <summary>Just the dispatchable type names, ordered — surfaced in SubagentRunner.AvailableTypes.</summary>
    public IReadOnlyList<string> Names =>
        _byName.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
}
