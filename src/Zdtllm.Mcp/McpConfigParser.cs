using System.Text.Json;

namespace Zdtllm.Mcp;

/// <summary>
/// Parses --mcp-config files and merges multiple configs together. Format mirrors
/// the Claude Code CLI:
///   { "mcpServers": { "name": { "command": "...", "args": [...], "env": {...} } } }
/// Later sources override earlier ones for the same server name (so CLI flags can
/// shadow settings.json entries). Unknown / extra fields are tolerated — the spec
/// ships with optional keys we don't care about (yet).
/// </summary>
public static class McpConfigParser
{
    /// <summary>
    /// Parse one JSON document (string contents — caller decides between file vs.
    /// inline). Returns the servers in declaration order. Throws
    /// <see cref="McpConfigException"/> on malformed input.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Parse(string json, string sourceLabel = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new McpConfigException($"{sourceLabel}: not valid JSON ({ex.Message}).", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new McpConfigException($"{sourceLabel}: top-level must be an object.");

            // mcpServers is required; an empty / missing object is treated as "no servers"
            // rather than an error so users can keep the key around.
            if (!doc.RootElement.TryGetProperty("mcpServers", out var serversNode))
                return Array.Empty<McpServerConfig>();
            if (serversNode.ValueKind == JsonValueKind.Null)
                return Array.Empty<McpServerConfig>();
            if (serversNode.ValueKind != JsonValueKind.Object)
                throw new McpConfigException($"{sourceLabel}: 'mcpServers' must be an object.");

            var result = new List<McpServerConfig>();
            foreach (var entry in serversNode.EnumerateObject())
            {
                result.Add(ParseServer(entry.Name, entry.Value, sourceLabel));
            }
            return result;
        }
    }

    /// <summary>
    /// Read a config file and parse. Convenience wrapper that includes the path in error
    /// messages so misconfigurations point users to the right file.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new McpConfigException($"--mcp-config: file not found: {path}");
        var content = File.ReadAllText(path);
        return Parse(content, sourceLabel: path);
    }

    /// <summary>
    /// Merge multiple parsed lists. Later entries with the same server name overwrite earlier
    /// ones — call order should mirror config precedence (settings.json first, CLI last).
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Merge(params IReadOnlyList<McpServerConfig>[] sources)
    {
        var by = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var source in sources)
        {
            foreach (var server in source)
            {
                if (!by.ContainsKey(server.Name)) order.Add(server.Name);
                by[server.Name] = server;
            }
        }
        return order.Select(n => by[n]).ToList();
    }

    private static McpServerConfig ParseServer(string name, JsonElement node, string sourceLabel)
    {
        if (node.ValueKind != JsonValueKind.Object)
            throw new McpConfigException($"{sourceLabel}: server '{name}' must be an object.");

        var command = node.TryGetProperty("command", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String
            ? cmdEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(command))
            throw new McpConfigException($"{sourceLabel}: server '{name}' is missing required 'command' string.");

        var args = new List<string>();
        if (node.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in argsEl.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.String)
                    throw new McpConfigException(
                        $"{sourceLabel}: server '{name}' has a non-string in 'args'.");
                args.Add(a.GetString()!);
            }
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in envEl.EnumerateObject())
            {
                env[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => throw new McpConfigException(
                        $"{sourceLabel}: server '{name}' has unsupported env value type for '{prop.Name}'."),
                };
            }
        }

        return new McpServerConfig(name, command!, args, env);
    }
}

public sealed class McpConfigException : Exception
{
    public McpConfigException(string message) : base(message) { }
    public McpConfigException(string message, Exception inner) : base(message, inner) { }
}
