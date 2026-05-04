using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Mcp;

/// <summary>
/// Wraps one tool exposed by an MCP server as an <see cref="ITool"/>. Naming follows
/// the Claude Code convention "mcp__&lt;server&gt;__&lt;tool&gt;" so multiple servers
/// can ship a tool called "search" without colliding. We forward the server's
/// inputSchema verbatim so the LLM sees the exact arguments the server expects.
///
/// CanRunInParallel is false: most MCP servers are single-process and can't reliably
/// handle interleaved tools/call requests, plus the JSON-RPC client serialises by id
/// which would defeat any parallelism anyway.
/// </summary>
public sealed class McpTool : ITool
{
    private readonly McpClient _client;
    private readonly string _remoteName;

    public McpTool(McpClient client, string serverName, McpToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(serverName);
        ArgumentNullException.ThrowIfNull(descriptor);

        _client = client;
        _remoteName = descriptor.Name;
        Schema = new ToolSchema(
            Name: $"mcp__{serverName}__{descriptor.Name}",
            Description: string.IsNullOrEmpty(descriptor.Description)
                ? $"MCP tool '{descriptor.Name}' from server '{serverName}'."
                : descriptor.Description,
            Parameters: descriptor.InputSchema);
    }

    public ToolSchema Schema { get; }

    public bool CanRunInParallel => false;

    public string? GetSpecifierForPermissions(JsonElement args) => null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var result = await _client.CallToolAsync(_remoteName, args, ct).ConfigureAwait(false);
            return result.IsError
                ? ToolResult.Error(result.Text)
                : ToolResult.Success(result.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpRpcException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"[mcp] {Schema.Name} crashed: {ex.Message}");
        }
    }
}
