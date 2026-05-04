using System.Collections.Concurrent;
using System.Text.Json;

namespace Zdtllm.Mcp;

/// <summary>
/// MCP server's tool listing entry. Mirrors the JSON-RPC schema returned by tools/list:
/// each tool has a name, an optional description, and a JSON-Schema describing its
/// arguments (which we forward verbatim to the LLM as the tool's parameters block).
/// </summary>
public sealed record McpToolDescriptor(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// JSON-RPC 2.0 client for one MCP server. Owns the transport, demuxes responses to
/// pending requests by id, and exposes the small slice of MCP we actually use:
/// initialize → tools/list → tools/call. Server-initiated requests (e.g. sampling)
/// are not yet supported — we ignore them safely.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IMcpTransport _transport;
    private readonly string _serverName;
    private readonly TextWriter _diagnostics;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerTask;
    private long _nextId;
    private string? _serverInfoName;
    private string? _serverInfoVersion;
    private bool _disposed;

    public McpClient(IMcpTransport transport, string serverName, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(serverName);
        _transport = transport;
        _serverName = serverName;
        _diagnostics = diagnostics ?? TextWriter.Null;
    }

    public string ServerName => _serverName;
    public string? ServerInfoName => _serverInfoName;
    public string? ServerInfoVersion => _serverInfoVersion;

    /// <summary>
    /// Run the MCP handshake: send initialize, await result, send the
    /// 'notifications/initialized' notification. Must be called once before tools/list.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        // The reader pump must be running before we send anything that expects a response.
        StartReaderIfNeeded();

        var initParams = new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "zdtllmcli", version = "1.0.0" },
        };
        var resp = await SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);

        if (resp.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                _serverInfoName = n.GetString();
            if (info.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                _serverInfoVersion = v.GetString();
        }

        await SendNotificationAsync("notifications/initialized", new { }, ct).ConfigureAwait(false);
    }

    /// <summary>List the tools the server exposes. Returns an empty list if the server has none.</summary>
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        var resp = await SendRequestAsync("tools/list", new { }, ct).ConfigureAwait(false);
        if (!resp.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return Array.Empty<McpToolDescriptor>();

        var result = new List<McpToolDescriptor>();
        foreach (var t in tools.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            var name = t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrEmpty(name)) continue;
            var desc = t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty : string.Empty;
            var schema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                ? s.Clone()
                : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
            result.Add(new McpToolDescriptor(name, desc, schema));
        }
        return result;
    }

    /// <summary>
    /// Invoke <paramref name="toolName"/> with the given JSON arguments object. Returns
    /// the server's textual response — concatenation of every text content block, plus
    /// a marker on isError=true so callers can distinguish.
    /// </summary>
    public async Task<McpCallResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        var resp = await SendRequestAsync("tools/call", new
        {
            name = toolName,
            arguments = arguments.ValueKind == JsonValueKind.Undefined ? new { } : (object)arguments,
        }, ct).ConfigureAwait(false);

        var isError = resp.TryGetProperty("isError", out var err)
                      && err.ValueKind == JsonValueKind.True;

        var sb = new System.Text.StringBuilder();
        if (resp.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                var type = part.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                    ? ty.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(tx.GetString());
                }
                else
                {
                    // Non-text parts (images, resources) — we can't render them, so
                    // include the raw JSON so the model at least sees what came back.
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(part.GetRawText());
                }
            }
        }
        return new McpCallResult(sb.ToString(), isError);
    }

    private void StartReaderIfNeeded()
    {
        if (_readerTask is not null) return;
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token));
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
                if (line is null) break; // EOF
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException ex)
                {
                    await _diagnostics.WriteLineAsync(
                        $"[mcp:{_serverName}] non-JSON line on stdout: {ex.Message}").ConfigureAwait(false);
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    // Responses have an id and either result or error. Notifications have
                    // a method but no id — we ignore them for now.
                    if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                        continue;
                    if (!idEl.TryGetInt64(out var id)) continue;
                    if (!_pending.TryRemove(id, out var tcs)) continue;

                    if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                    {
                        var msg = errEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                            ? m.GetString() ?? "unknown error"
                            : "unknown error";
                        tcs.TrySetException(new McpRpcException(_serverName, msg, errEl.Clone()));
                    }
                    else if (root.TryGetProperty("result", out var resultEl))
                    {
                        tcs.TrySetResult(resultEl.Clone());
                    }
                    else
                    {
                        tcs.TrySetException(new McpRpcException(_serverName, "response missing both result and error.", default));
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            // Fan out the error to anyone still waiting so they don't hang forever.
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new McpRpcException(_serverName, $"reader loop crashed: {ex.Message}", default));
            _pending.Clear();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object @params, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McpClient));
        StartReaderIfNeeded();

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        }, JsonOpts);

        // If SendAsync throws (transport faulted, peer hung up), the pending entry is now
        // orphaned — nothing will ever wake the tcs. Remove it on any send failure so the
        // dictionary doesn't accumulate forever during 12-hour runs with intermittent faults.
        try
        {
            await _transport.SendAsync(json, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using (ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(ct);
        }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params,
        }, JsonOpts);
        await _transport.SendAsync(json, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _readerCts.Cancel(); } catch { /* swallow */ }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _readerCts.Dispose();
        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch { /* swallow */ }
        }
    }
}

public sealed record McpCallResult(string Text, bool IsError);

public sealed class McpRpcException : Exception
{
    public string ServerName { get; }
    public JsonElement RawError { get; }
    public McpRpcException(string serverName, string message, JsonElement rawError)
        : base($"[mcp:{serverName}] {message}")
    {
        ServerName = serverName;
        RawError = rawError;
    }
}
