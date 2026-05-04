namespace Zdtllm.Mcp;

/// <summary>
/// Wire-level shuttle for MCP messages. Implementations decide HOW the JSON moves
/// (subprocess stdin/stdout, in-memory pipes for tests, etc.); McpClient stays
/// transport-agnostic. Each call writes / reads a SINGLE complete JSON-RPC message
/// — framing (newline-delimited JSON) is the implementer's responsibility.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>Returns the next complete message, or null at end of stream.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}
