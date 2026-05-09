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

    /// <summary>
    /// Best-effort snapshot of the last few KB the subprocess wrote to stderr. Used to
    /// enrich error messages when the handshake / init times out — without this, callers
    /// see only "A task was canceled." with no hint at what the server actually logged.
    /// In-memory transports may return empty.
    /// </summary>
    string StderrTail() => string.Empty;
}
