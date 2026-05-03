using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Zdtllm.Tools;

public sealed record TodoItem(string Id, string Content, string Status, string? ActiveForm = null);

public sealed class TodoWriteTool : ITool
{
    private static readonly ImmutableHashSet<string> ValidStatuses =
        ImmutableHashSet.Create(StringComparer.Ordinal, "pending", "in_progress", "completed");

    private readonly List<TodoItem> _todos = new();
    private readonly object _lock = new();

    /// <summary>Snapshot of the current todo list. Empty before any TodoWrite call.</summary>
    public IReadOnlyList<TodoItem> CurrentTodos
    {
        get { lock (_lock) return _todos.ToList(); }
    }

    public ToolSchema Schema { get; } = new(
        Name: "TodoWrite",
        Description: "Replace the agent's working todo list. Each todo has an id, content, and status (\"pending\" | \"in_progress\" | \"completed\"). Useful for tracking multi-step work.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                todos = new
                {
                    type = "array",
                    description = "Replacement list of todos.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "Stable identifier for the todo." },
                            content = new { type = "string", description = "What needs to be done." },
                            status = new { type = "string", description = "pending | in_progress | completed." },
                            activeForm = new { type = "string", description = "Optional present-continuous form (\"Running tests\")." },
                        },
                        required = new[] { "content", "status" },
                    },
                },
            },
            required = new[] { "todos" },
        }));

    /// <summary>Todo list is shared mutable state — concurrent rewrites would clobber each other.</summary>
    public bool CanRunInParallel => false;

    /// <summary>Each subagent maintains its own todo list independent of the parent's.</summary>
    public ITool CloneForSubagent() => new TodoWriteTool();

    public string? GetSpecifierForPermissions(JsonElement args) => null;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("todos", out var todosEl) || todosEl.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolResult.Error("TodoWrite: missing or invalid 'todos' array."));

        var parsed = new List<TodoItem>();
        var idx = 0;
        foreach (var el in todosEl.EnumerateArray())
        {
            idx++;
            if (el.ValueKind != JsonValueKind.Object)
                return Task.FromResult(ToolResult.Error($"TodoWrite: todo #{idx} is not an object."));

            var content = GetString(el, "content");
            var status = GetString(el, "status");
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(ToolResult.Error($"TodoWrite: todo #{idx} is missing 'content'."));
            if (string.IsNullOrWhiteSpace(status))
                return Task.FromResult(ToolResult.Error($"TodoWrite: todo #{idx} is missing 'status'."));
            if (!ValidStatuses.Contains(status))
                return Task.FromResult(ToolResult.Error(
                    $"TodoWrite: todo #{idx} has invalid status '{status}'. Expected one of: pending, in_progress, completed."));

            var id = GetString(el, "id") ?? idx.ToString();
            var activeForm = GetString(el, "activeForm");
            parsed.Add(new TodoItem(id, content!, status!, activeForm));
        }

        lock (_lock)
        {
            _todos.Clear();
            _todos.AddRange(parsed);
        }

        return Task.FromResult(ToolResult.Success(FormatSummary(parsed)));
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FormatSummary(IReadOnlyList<TodoItem> todos)
    {
        if (todos.Count == 0) return "Todo list cleared.";

        var pending = todos.Count(t => t.Status == "pending");
        var inProgress = todos.Count(t => t.Status == "in_progress");
        var completed = todos.Count(t => t.Status == "completed");

        var sb = new StringBuilder();
        sb.AppendLine($"Updated {todos.Count} todo(s): {pending} pending, {inProgress} in progress, {completed} completed.");
        foreach (var t in todos)
        {
            var marker = t.Status switch
            {
                "completed" => "[x]",
                "in_progress" => "[~]",
                _ => "[ ]",
            };
            sb.AppendLine($"  {marker} {t.Content}");
        }
        return sb.ToString().TrimEnd();
    }
}
