using System.Text.Json;

namespace Zdtllm.Tools;

/// <summary>
/// Stub. The MVP doesn't ship with a default search provider — wiring DuckDuckGo's
/// Instant-Answer API would only return useful results for definitions and the
/// proper search APIs (Brave, Serper, Tavily) all need a per-user API key. Instead,
/// this tool returns a structured "configure a provider" message so the model can
/// recover by either using WebFetch directly against a known URL or falling back
/// to its training data.
/// </summary>
public sealed class WebSearchTool : ITool
{
    public ToolSchema Schema { get; } = new(
        Name: "WebSearch",
        Description: "Run a web search and return the top results. NOTE: not configured by default — configure a search provider in settings (planned). Falls back to a clear error message until then.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query." },
            },
            required = new[] { "query" },
        }));

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString()
            : null;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("WebSearch: missing 'query' parameter."));

        return Task.FromResult(ToolResult.Error(
            "WebSearch is not configured. Configure a search provider (Brave / Serper / Tavily) in " +
            ".zdtllm/settings.json (litellm.search.*) — not yet implemented in this version. " +
            "If you have a known URL, use WebFetch instead."));
    }
}
