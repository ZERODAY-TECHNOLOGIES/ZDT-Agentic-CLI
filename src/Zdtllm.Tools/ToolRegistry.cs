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
}
