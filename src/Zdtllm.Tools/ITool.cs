using System.Text.Json;

namespace Zdtllm.Tools;

public sealed record ToolSchema(string Name, string Description, JsonElement Parameters);

/// <summary>
/// Per-turn execution context handed to every tool. <see cref="Cwd"/> is the working
/// directory the agent considers home (the ambient process cwd at turn start). <see
/// cref="Model"/> is the resolved model id of the session that's currently driving the
/// turn — populated by AgentLoop from <c>session.Model</c> so a /model switch propagates
/// to tools that need to know it (notably TaskTool, which uses it as the subagent's model
/// instead of the parent's stale startup option).
/// </summary>
public sealed record ToolContext(string Cwd, string? Model = null);

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

    /// <summary>
    /// True when the tool drives the interactive console itself (reads keystrokes / renders a
    /// selectable list). AgentLoop must NOT wrap such a call in a Spectre <c>Status</c> spinner —
    /// nesting an interactive prompt inside a live status region throws "Trying to run one or
    /// more interactive functions concurrently". Interactive tools should also return
    /// <see cref="CanRunInParallel"/> == false. Default: false.
    /// </summary>
    bool IsInteractive => false;
}
