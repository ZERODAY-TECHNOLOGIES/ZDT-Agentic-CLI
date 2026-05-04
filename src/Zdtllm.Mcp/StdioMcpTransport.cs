using System.Diagnostics;
using System.Text;

namespace Zdtllm.Mcp;

/// <summary>
/// Spawns an MCP server subprocess and shuttles newline-delimited JSON messages
/// between the parent and the server over stdin/stdout. stderr is forwarded to a
/// caller-supplied sink (defaults to discard) so a misbehaving server doesn't
/// pollute the agent's REPL.
///
/// MCP's stdio transport is line-delimited JSON — each message is one JSON object
/// terminated by '\n'. We use UTF-8 explicitly because the spec mandates it and
/// .NET's default Process redirection encoding is OS-dependent.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly TextWriter _stderrSink;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _stderrPump;
    private readonly object _writeLock = new();
    private bool _disposed;

    private StdioMcpTransport(Process process, StreamWriter stdin, StreamReader stdout, TextWriter stderrSink)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
        _stderrSink = stderrSink;

        // Drain stderr on a background pump so a chatty server can't fill the pipe and
        // deadlock. We forward to the caller's sink for diagnosability. Pump is cancellable
        // so DisposeAsync can stop it cleanly when the child holds stderr open after stdin
        // is closed (otherwise the pump would linger as a fire-and-forget task).
        var pumpToken = _pumpCts.Token;
        _stderrPump = Task.Run(async () =>
        {
            try
            {
                string? line;
                while (!pumpToken.IsCancellationRequested
                    && (line = await _process.StandardError.ReadLineAsync(pumpToken).ConfigureAwait(false)) is not null)
                {
                    await _stderrSink.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort — if the server closes stderr or we're cancelled, just stop pumping.
            }
        }, pumpToken);
    }

    public static StdioMcpTransport Start(McpServerConfig config, TextWriter? stderrSink = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Force UTF-8 — Windows defaults to the system codepage which corrupts non-ASCII.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CreateNoWindow = true,
        };
        foreach (var a in config.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in config.Env) psi.Environment[k] = v;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"MCP server '{config.Name}': failed to spawn '{config.Command}'.");

        // If the constructor body throws (e.g. OOM allocating the pump task) we must NOT
        // leak the live subprocess. Wrap construction so we can kill+dispose on failure.
        try
        {
            return new StdioMcpTransport(
                process,
                process.StandardInput,
                process.StandardOutput,
                stderrSink ?? TextWriter.Null);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* swallow */ }
            try { process.Dispose(); } catch { /* swallow */ }
            throw;
        }
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        ct.ThrowIfCancellationRequested();

        // StreamWriter.WriteLineAsync isn't thread-safe; the JSON-RPC client serialises
        // its own writes per request, but we still gate on a lock to be safe against
        // notification senders racing with request senders.
        lock (_writeLock)
        {
            _stdin.WriteLine(json);
            _stdin.Flush();
        }
        await Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) =>
        await _stdout.ReadLineAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _stdin.Close(); } catch { /* swallow */ }
        try
        {
            if (!_process.HasExited)
            {
                // Give the server a beat to flush — most MCP servers exit cleanly when
                // stdin closes. If it lingers, kill it; we're shutting down anyway.
                if (!_process.WaitForExit(500))
                {
                    try { _process.Kill(entireProcessTree: true); } catch { /* swallow */ }
                }
            }
        }
        catch { /* swallow */ }

        // Signal the stderr pump to exit so it doesn't linger as an orphan task on a child
        // that closes stdout but holds stderr open.
        try { _pumpCts.Cancel(); } catch { /* swallow */ }
        try { await _stderrPump.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
        catch { /* swallow */ }

        _pumpCts.Dispose();
        _process.Dispose();
    }
}
