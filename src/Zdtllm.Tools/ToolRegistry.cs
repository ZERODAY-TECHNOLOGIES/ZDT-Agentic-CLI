using System.Collections.Immutable;

namespace Zdtllm.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Schema.Name] = tool;
    }

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    public ImmutableArray<ITool> All => [.._tools.Values];

    public ImmutableArray<ToolSchema> Schemas =>
        [.._tools.Values.Select(t => t.Schema)];

    /// <summary>
    /// Drop the named tool from the registry. Returns true if it was present. Used by
    /// the --tools allowlist after every builtin / MCP / Task tool has been registered.
    /// </summary>
    public bool Remove(string name) => _tools.Remove(name);
}
