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
}
