namespace Zdtllm.Mcp;

/// <summary>
/// One server entry from a Claude-Code-style mcpServers config block. We support
/// stdio transport — the dominant kind in the wild — by spawning the given command
/// with the given args. Env vars are passed through on top of the parent's environment.
/// </summary>
public sealed record McpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env);
