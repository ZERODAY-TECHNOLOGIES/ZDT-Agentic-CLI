using System.Text.Json;
using System.Threading.Channels;
using Zdtllm.Mcp;

namespace Zdtllm.Core.Tests.Mcp;

/// <summary>
/// Drive McpClient with an in-memory transport. Each test scripts the server side as a
/// list of canned responses keyed off the inbound request's id, then asserts that the
/// client's high-level methods (initialize / list / call) round-trip cleanly.
/// </summary>
public sealed class McpClientTests
{
    [Fact]
    public async Task Initialize_records_serverInfo_from_handshake_and_sends_initialized_notification()
    {
        var transport = new ScriptedTransport(req =>
        {
            // First request must be initialize; respond with the canonical handshake payload.
            using var doc = JsonDocument.Parse(req);
            doc.RootElement.GetProperty("method").GetString().Should().Be("initialize");
            var id = doc.RootElement.GetProperty("id").GetInt64();
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id,
                result = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "spec-server", version = "0.0.1" },
                },
            });
        });

        await using var client = new McpClient(transport, "spec-server");
        await client.InitializeAsync(CancellationToken.None);

        client.ServerInfoName.Should().Be("spec-server");
        client.ServerInfoVersion.Should().Be("0.0.1");

        // The 'initialized' notification was sent and has no id (notifications don't).
        transport.Sent.Should().HaveCount(2);
        using var notif = JsonDocument.Parse(transport.Sent[1]);
        notif.RootElement.TryGetProperty("id", out _).Should().BeFalse();
        notif.RootElement.GetProperty("method").GetString().Should().Be("notifications/initialized");
    }

    [Fact]
    public async Task ListTools_returns_descriptors_with_the_inputSchema_intact()
    {
        var transport = new ScriptedTransport(req =>
        {
            using var doc = JsonDocument.Parse(req);
            var method = doc.RootElement.GetProperty("method").GetString();
            var id = doc.RootElement.GetProperty("id").GetInt64();
            return method switch
            {
                "initialize" => JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "s", version = "1" } },
                }),
                "tools/list" => JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new
                    {
                        tools = new object[]
                        {
                            new
                            {
                                name = "search",
                                description = "Search the index.",
                                inputSchema = new
                                {
                                    type = "object",
                                    properties = new { q = new { type = "string" } },
                                    required = new[] { "q" },
                                },
                            },
                        },
                    },
                }),
                _ => throw new InvalidOperationException("unexpected method " + method),
            };
        });

        await using var client = new McpClient(transport, "s");
        await client.InitializeAsync(CancellationToken.None);
        var tools = await client.ListToolsAsync(CancellationToken.None);

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("search");
        tools[0].Description.Should().Be("Search the index.");
        // Schema preserves type/properties/required.
        tools[0].InputSchema.GetProperty("type").GetString().Should().Be("object");
        tools[0].InputSchema.GetProperty("required")[0].GetString().Should().Be("q");
    }

    [Fact]
    public async Task CallTool_concatenates_text_content_blocks()
    {
        var transport = new ScriptedTransport(req =>
        {
            using var doc = JsonDocument.Parse(req);
            var method = doc.RootElement.GetProperty("method").GetString();
            var id = doc.RootElement.GetProperty("id").GetInt64();
            if (method == "initialize")
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "s", version = "1" } },
                });
            if (method == "tools/call")
            {
                doc.RootElement.GetProperty("params").GetProperty("name").GetString().Should().Be("echo");
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new
                    {
                        content = new object[]
                        {
                            new { type = "text", text = "first line" },
                            new { type = "text", text = "second line" },
                        },
                        isError = false,
                    },
                });
            }
            throw new InvalidOperationException("unexpected " + method);
        });

        await using var client = new McpClient(transport, "s");
        await client.InitializeAsync(CancellationToken.None);

        using var argsDoc = JsonDocument.Parse("""{"text":"hi"}""");
        var result = await client.CallToolAsync("echo", argsDoc.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("first line");
        result.Text.Should().Contain("second line");
    }

    [Fact]
    public async Task CallTool_propagates_isError_flag_for_server_side_failures()
    {
        var transport = new ScriptedTransport(req =>
        {
            using var doc = JsonDocument.Parse(req);
            var method = doc.RootElement.GetProperty("method").GetString();
            var id = doc.RootElement.GetProperty("id").GetInt64();
            if (method == "initialize")
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "s", version = "1" } },
                });
            if (method == "tools/call")
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new
                    {
                        content = new[] { new { type = "text", text = "tool blew up" } },
                        isError = true,
                    },
                });
            throw new InvalidOperationException();
        });

        await using var client = new McpClient(transport, "s");
        await client.InitializeAsync(CancellationToken.None);
        var result = await client.CallToolAsync("boom", default, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("tool blew up");
    }

    [Fact]
    public async Task RPC_error_response_throws_McpRpcException_with_server_message()
    {
        var transport = new ScriptedTransport(req =>
        {
            using var doc = JsonDocument.Parse(req);
            var id = doc.RootElement.GetProperty("id").GetInt64();
            // Initialize succeeds, but tools/list returns a JSON-RPC error.
            var method = doc.RootElement.GetProperty("method").GetString();
            if (method == "initialize")
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id,
                    result = new { protocolVersion = "2024-11-05", capabilities = new { }, serverInfo = new { name = "s", version = "1" } },
                });
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id,
                error = new { code = -32603, message = "internal blam" },
            });
        });

        await using var client = new McpClient(transport, "s");
        await client.InitializeAsync(CancellationToken.None);

        var act = async () => await client.ListToolsAsync(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<McpRpcException>();
        ex.Which.Message.Should().Contain("internal blam");
        ex.Which.ServerName.Should().Be("s");
    }

    /// <summary>
    /// In-memory IMcpTransport. The supplied responder runs synchronously when SendAsync
    /// arrives — its return value is queued as the next ReceiveAsync line. This is enough
    /// to drive a request/response client; notifications (no expected response) are dropped
    /// by the responder returning null.
    /// </summary>
    private sealed class ScriptedTransport : IMcpTransport
    {
        private readonly Func<string, string?> _responder;
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        private readonly List<string> _sent = new();
        private bool _disposed;

        public ScriptedTransport(Func<string, string?> responder) { _responder = responder; }

        public IReadOnlyList<string> Sent => _sent;

        public Task SendAsync(string json, CancellationToken ct)
        {
            _sent.Add(json);
            // Notifications don't have an id — for those we return null and skip queuing.
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("id", out _)) return Task.CompletedTask;
            var response = _responder(json);
            if (response is not null)
                _inbound.Writer.TryWrite(response);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            try
            {
                if (await _inbound.Reader.WaitToReadAsync(ct))
                    return _inbound.Reader.TryRead(out var msg) ? msg : null;
            }
            catch (ChannelClosedException) { }
            return null;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
