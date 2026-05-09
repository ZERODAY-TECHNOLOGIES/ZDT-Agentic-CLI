using Zdtllm.Tools;

namespace Zdtllm.Mcp;

/// <summary>
/// Owns the lifetime of a set of MCP servers — spawn, hand-shake, list tools,
/// register them in the parent agent's <see cref="ToolRegistry"/>, and shut
/// everything down on dispose.
///
/// Failures in one server never abort the whole launch: each server is started
/// in isolation and its outcome (Connected / Failed) is recorded so the CLI
/// can surface a useful summary line per server.
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = new();
    private readonly List<McpServerStatus> _statuses = new();
    private readonly TextWriter _diagnostics;
    private bool _disposed;

    public McpManager(TextWriter? diagnostics = null)
    {
        _diagnostics = diagnostics ?? TextWriter.Null;
    }

    public IReadOnlyList<McpServerStatus> Statuses => _statuses;

    /// <summary>
    /// Boot every configured server, then register their tools into <paramref name="registry"/>.
    /// Each server gets a per-server timeout so a hanging initialize/tools-list doesn't block the
    /// whole CLI startup. We swallow individual server failures so the agent stays usable.
    /// </summary>
    public async Task StartAndRegisterAsync(
        IReadOnlyList<McpServerConfig> servers,
        ToolRegistry registry,
        TimeSpan handshakeTimeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var server in servers)
        {
            using var perServerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perServerCts.CancelAfter(handshakeTimeout);

            // Track transport AND client separately. If `new McpClient(...)` throws between
            // Start and the catch, the transport is live and must be disposed explicitly —
            // disposing the client wouldn't dispose the transport because the client never
            // got constructed.
            StdioMcpTransport? transport = null;
            McpClient? client = null;
            try
            {
                transport = StdioMcpTransport.Start(server, _diagnostics);
                client = new McpClient(transport, server.Name, _diagnostics);
                await client.InitializeAsync(perServerCts.Token).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(perServerCts.Token).ConfigureAwait(false);

                foreach (var t in tools)
                    registry.Register(new McpTool(client, server.Name, t));

                _clients.Add(client);
                _statuses.Add(new McpServerStatus(
                    Name: server.Name,
                    Connected: true,
                    ToolCount: tools.Count,
                    ServerInfo: $"{client.ServerInfoName ?? "?"}/{client.ServerInfoVersion ?? "?"}",
                    ErrorMessage: null));
            }
            catch (OperationCanceledException) when (
                perServerCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Per-server timeout fired (the linked-token CTS cancelled itself via CancelAfter)
                // — distinguish it from caller cancellation so the operator gets a concrete,
                // actionable message instead of the .NET default "A task was canceled." Include
                // the last bit of stderr the subprocess produced if any: a Python traceback or
                // PHP fatal usually identifies the real problem in one line.
                var tail = transport?.StderrTail() ?? string.Empty;
                if (client is not null)
                    await client.DisposeAsync().ConfigureAwait(false);
                else if (transport is not null)
                    await transport.DisposeAsync().ConfigureAwait(false);
                var seconds = (int)Math.Round(handshakeTimeout.TotalSeconds);
                var msg = $"init timed out after {seconds}s (handshake never completed). " +
                          $"Increase --mcp-init-timeout-seconds or mcp.initTimeoutSeconds for slow-booting servers.";
                if (tail.Length > 0) msg += "\n  last stderr:\n    " + tail.Replace("\n", "\n    ");
                _statuses.Add(new McpServerStatus(
                    Name: server.Name,
                    Connected: false,
                    ToolCount: 0,
                    ServerInfo: null,
                    ErrorMessage: msg));
            }
            catch (Exception ex)
            {
                var tail = transport?.StderrTail() ?? string.Empty;
                if (client is not null)
                    await client.DisposeAsync().ConfigureAwait(false);
                else if (transport is not null)
                    await transport.DisposeAsync().ConfigureAwait(false);
                var msg = ex.Message;
                if (tail.Length > 0) msg += "\n  last stderr:\n    " + tail.Replace("\n", "\n    ");
                _statuses.Add(new McpServerStatus(
                    Name: server.Name,
                    Connected: false,
                    ToolCount: 0,
                    ServerInfo: null,
                    ErrorMessage: msg));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var c in _clients)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
        }
        _clients.Clear();
    }
}

public sealed record McpServerStatus(
    string Name,
    bool Connected,
    int ToolCount,
    string? ServerInfo,
    string? ErrorMessage);
