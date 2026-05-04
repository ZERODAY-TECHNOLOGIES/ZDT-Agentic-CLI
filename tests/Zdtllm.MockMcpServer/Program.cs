using System.Text;
using System.Text.Json;

// A minimal MCP server used by integration tests. Speaks newline-delimited JSON-RPC 2.0
// on stdin/stdout. Implements:
//   - initialize           → returns protocolVersion + serverInfo + capabilities
//   - tools/list           → returns one tool: "echo" (echoes its 'text' argument)
//                            and one tool: "boom" (always returns isError=true)
//   - tools/call           → dispatches to the named tool
//   - notifications/*      → ignored (no response)
// This is intentionally tiny so it makes a clean fixture for end-to-end tests against
// our McpManager / McpClient. Anything we don't recognise gets a JSON-RPC method-not-found
// error so the client surface stays well-behaved under test.

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

string? line;
while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    JsonDocument doc;
    try { doc = JsonDocument.Parse(line); }
    catch { continue; }

    using (doc)
    {
        var root = doc.RootElement;
        var hasId = root.TryGetProperty("id", out var idEl);
        var method = root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString() : null;

        // Notifications: no id, no response. Just ignore.
        if (!hasId)
        {
            continue;
        }

        switch (method)
        {
            case "initialize":
                Respond(idEl, new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "mock", version = "0.1.0" },
                });
                break;

            case "tools/list":
                Respond(idEl, new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "echo",
                            description = "Echo back the input string.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    text = new { type = "string", description = "Text to echo." },
                                },
                                required = new[] { "text" },
                            },
                        },
                        new
                        {
                            name = "boom",
                            description = "Always errors — for testing error paths.",
                            inputSchema = new { type = "object", properties = new { } },
                        },
                    },
                });
                break;

            case "tools/call":
                {
                    var p = root.GetProperty("params");
                    var name = p.GetProperty("name").GetString();
                    var callArgs = p.TryGetProperty("arguments", out var aEl) ? aEl : default;

                    if (name == "echo")
                    {
                        var text = callArgs.ValueKind == JsonValueKind.Object
                                   && callArgs.TryGetProperty("text", out var t)
                                   && t.ValueKind == JsonValueKind.String
                            ? t.GetString() ?? string.Empty
                            : string.Empty;
                        Respond(idEl, new
                        {
                            content = new[] { new { type = "text", text = $"echo:{text}" } },
                            isError = false,
                        });
                    }
                    else if (name == "boom")
                    {
                        Respond(idEl, new
                        {
                            content = new[] { new { type = "text", text = "synthetic boom" } },
                            isError = true,
                        });
                    }
                    else
                    {
                        RespondError(idEl, -32601, $"unknown tool: {name}");
                    }
                }
                break;

            default:
                RespondError(idEl, -32601, $"method not found: {method}");
                break;
        }
    }
}

static void Respond(JsonElement id, object result)
{
    var payload = new
    {
        jsonrpc = "2.0",
        id = ConvertId(id),
        result,
    };
    Console.Out.WriteLine(JsonSerializer.Serialize(payload));
    Console.Out.Flush();
}

static void RespondError(JsonElement id, int code, string message)
{
    var payload = new
    {
        jsonrpc = "2.0",
        id = ConvertId(id),
        error = new { code, message },
    };
    Console.Out.WriteLine(JsonSerializer.Serialize(payload));
    Console.Out.Flush();
}

static object? ConvertId(JsonElement id) => id.ValueKind switch
{
    JsonValueKind.Number => id.GetInt64(),
    JsonValueKind.String => id.GetString(),
    _ => null,
};
