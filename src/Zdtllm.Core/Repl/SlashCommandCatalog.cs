namespace Zdtllm.Core.Repl;

/// <summary>One slash command as advertised to the autocomplete picker: its name and a blurb.</summary>
public sealed record SlashCommandInfo(string Name, string Description);

/// <summary>
/// The canonical list of interactive slash commands, shared between the REPL's dispatcher and the
/// "type <c>/</c> to autocomplete" picker so both stay in sync. Ordered roughly by how often each
/// is reached.
/// </summary>
public static class SlashCommandCatalog
{
    public static IReadOnlyList<SlashCommandInfo> All { get; } = new[]
    {
        new SlashCommandInfo("/help", "show the command list"),
        new SlashCommandInfo("/model", "switch the model used by the next turn"),
        new SlashCommandInfo("/plan", "toggle plan mode (read-only; propose a plan before changes)"),
        new SlashCommandInfo("/workflow", "run a multi-agent workflow (/workflow <name> key=value …)"),
        new SlashCommandInfo("/workflows", "list declarative workflows in .zdtllm/workflows/"),
        new SlashCommandInfo("/context", "show context-window usage and per-role breakdown"),
        new SlashCommandInfo("/compact", "summarize older turns to free context"),
        new SlashCommandInfo("/status", "show session id, model, mode, message count"),
        new SlashCommandInfo("/tool-calling", "switch tool-call transport (native | xml)"),
        new SlashCommandInfo("/permissions", "show the current permission rule set"),
        new SlashCommandInfo("/mcp", "show connected MCP servers and their tool counts"),
        new SlashCommandInfo("/agents", "list available subagent types and their tool sets"),
        new SlashCommandInfo("/clear", "drop conversation history (system prompt kept)"),
        new SlashCommandInfo("/init", "create ZDTLLM.md (project memory file) in the cwd"),
        new SlashCommandInfo("/exit", "leave the REPL"),
    };
}
