using System.Text.Json;

namespace Zdtllm.Tools;

public sealed record ToolSchema(string Name, string Description, JsonElement Parameters);

public sealed record ToolContext(string Cwd);

public sealed record ToolResult(string Content, bool IsError)
{
    public static ToolResult Success(string content) => new(content, IsError: false);
    public static ToolResult Error(string message) => new(message, IsError: true);
}

public interface ITool
{
    ToolSchema Schema { get; }

    /// <summary>
    /// Returns the specifier string used for permission rule matching
    /// (e.g. the path for Read/Write/Edit, the command for Bash). Null means
    /// the tool has no natural specifier and rules of the form Tool(spec) won't match.
    /// </summary>
    string? GetSpecifierForPermissions(JsonElement args);

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct);

    /// <summary>
    /// True when concurrent calls to the SAME tool instance are safe. AgentLoop
    /// parallelises a turn's tool calls only if every tool involved in the batch
    /// returns true. Tools with mutable instance state (Bash's working dir,
    /// TodoWriteTool's todo list) and tools that race on a shared file (Edit / Write
    /// targeting the same path) should return false. Default: true.
    /// </summary>
    bool CanRunInParallel => true;

    /// <summary>
    /// Returns an instance to use inside a subagent. Stateless tools return <c>this</c>
    /// (their default). Stateful tools (BashTool's cwd, TodoWriteTool's list) return a
    /// fresh instance so the subagent can mutate without affecting the parent. Defaults
    /// to <c>this</c>.
    /// </summary>
    ITool CloneForSubagent() => this;
}
