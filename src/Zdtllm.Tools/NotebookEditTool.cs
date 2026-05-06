using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zdtllm.Tools;

/// <summary>
/// Edit a single cell inside a Jupyter <c>.ipynb</c> notebook. Mirrors the claude-cli
/// NotebookEdit tool 1:1 so prompts and tool allow-lists written for claude (e.g.
/// <c>--tools NotebookEdit</c>) reach zdt without modification.
///
/// Modes:
///   <list type="bullet">
///     <item><c>replace</c> (default) — overwrite the source of the cell whose <c>id</c> matches
///       <c>cell_id</c>. <c>cell_type</c> is optional; when omitted the existing type is preserved.</item>
///     <item><c>insert</c> — insert a new cell AFTER the cell whose <c>id</c> matches <c>cell_id</c>.
///       When <c>cell_id</c> is omitted, the new cell goes at index 0. <c>cell_type</c> is required.</item>
///     <item><c>delete</c> — drop the cell whose <c>id</c> matches <c>cell_id</c>.</item>
///   </list>
///
/// Notebook format details we preserve:
///   <list type="bullet">
///     <item>Top-level keys (<c>nbformat</c>, <c>nbformat_minor</c>, <c>metadata</c>) are left untouched.</item>
///     <item>Cell <c>source</c> is emitted as a string-array split on \n with trailing-\n on each
///       non-final line — this is the canonical shape <c>jupyter nbconvert</c> writes, and many
///       diff tools choke on the alternative single-string form.</item>
///     <item>Inserted code cells get <c>execution_count: null</c> and <c>outputs: []</c>.</item>
///     <item>Inserted cells get a fresh short id (8 hex chars) when none is provided.</item>
///   </list>
/// </summary>
public sealed class NotebookEditTool : ITool
{
    public ToolSchema Schema { get; } = new(
        Name: "NotebookEdit",
        Description:
            "Edit a cell inside a Jupyter notebook (.ipynb). " +
            "edit_mode=replace overwrites the named cell's source (cell_type optional, preserves existing type when omitted). " +
            "edit_mode=insert adds a new cell AFTER the cell named by cell_id (or at the start when cell_id is omitted); cell_type is required. " +
            "edit_mode=delete removes the cell named by cell_id.",
        Parameters: JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                notebook_path = new { type = "string", description = "Absolute or relative path to the .ipynb file." },
                cell_id = new { type = "string", description = "ID of the cell to replace, insert after, or delete. Optional only for insert-at-start." },
                new_source = new { type = "string", description = "New cell source. Required for replace and insert; ignored for delete." },
                cell_type = new { type = "string", description = "'code' or 'markdown'. Required for insert; optional for replace (preserves existing)." },
                edit_mode = new { type = "string", description = "'replace' (default), 'insert', or 'delete'." },
            },
            required = new[] { "notebook_path" },
        }));

    /// <summary>
    /// NotebookEdit is read-modify-write on a single .ipynb file — two concurrent calls to
    /// the same path lose the first write the same way Edit does. Same conservative default.
    /// </summary>
    public bool CanRunInParallel => false;

    public string? GetSpecifierForPermissions(JsonElement args) =>
        args.TryGetProperty("notebook_path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct)
    {
        if (!args.TryGetProperty("notebook_path", out var np) || np.ValueKind != JsonValueKind.String)
            return ToolResult.Error("NotebookEdit: missing or invalid 'notebook_path' parameter.");

        var rawPath = np.GetString()!;
        var fullPath = Path.IsPathRooted(rawPath) ? rawPath : Path.GetFullPath(Path.Combine(ctx.Cwd, rawPath));

        if (!File.Exists(fullPath))
            return ToolResult.Error($"NotebookEdit: notebook not found: {rawPath}");

        // We don't enforce a .ipynb extension hard — Jupyter sometimes saves with .json — but
        // we DO require the file to parse as a notebook (root object with a "cells" array).
        // That's the actual contract callers care about; the extension is just a hint.
        var editMode = ReadString(args, "edit_mode") ?? "replace";
        var cellId = ReadString(args, "cell_id");
        var cellType = ReadString(args, "cell_type");
        var newSource = ReadString(args, "new_source");

        if (editMode is not ("replace" or "insert" or "delete"))
            return ToolResult.Error($"NotebookEdit: invalid edit_mode '{editMode}'. Must be 'replace', 'insert', or 'delete'.");

        if (cellType is not null and not ("code" or "markdown"))
            return ToolResult.Error($"NotebookEdit: invalid cell_type '{cellType}'. Must be 'code' or 'markdown'.");

        if (editMode is "replace" or "insert" && newSource is null)
            return ToolResult.Error($"NotebookEdit: 'new_source' is required for edit_mode='{editMode}'.");

        if (editMode == "delete" && string.IsNullOrEmpty(cellId))
            return ToolResult.Error("NotebookEdit: 'cell_id' is required for edit_mode='delete'.");

        if (editMode == "insert" && cellType is null)
            return ToolResult.Error("NotebookEdit: 'cell_type' is required for edit_mode='insert'.");

        if (editMode == "replace" && string.IsNullOrEmpty(cellId))
            return ToolResult.Error("NotebookEdit: 'cell_id' is required for edit_mode='replace'.");

        JsonNode? root;
        try
        {
            var contents = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            root = JsonNode.Parse(contents);
        }
        catch (JsonException ex)
        {
            return ToolResult.Error($"NotebookEdit: failed to parse '{rawPath}' as JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"NotebookEdit: failed to read '{rawPath}': {ex.Message}");
        }

        if (root is not JsonObject obj)
            return ToolResult.Error($"NotebookEdit: '{rawPath}' is not a valid notebook (root is not an object).");

        if (obj["cells"] is not JsonArray cells)
            return ToolResult.Error($"NotebookEdit: '{rawPath}' is not a valid notebook (missing 'cells' array).");

        string summary;
        try
        {
            summary = editMode switch
            {
                "replace" => ApplyReplace(cells, cellId!, cellType, newSource!),
                "insert"  => ApplyInsert(cells, cellId, cellType!, newSource!),
                "delete"  => ApplyDelete(cells, cellId!),
                _         => throw new InvalidOperationException("unreachable"),
            };
        }
        catch (NotebookEditException ex)
        {
            return ToolResult.Error($"NotebookEdit: {ex.Message}");
        }

        try
        {
            // Jupyter writes 1-space indented JSON with a trailing newline. We match that so
            // diffs against an editor-saved notebook stay minimal.
            var serialized = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            await File.WriteAllTextAsync(fullPath, serialized + "\n", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"NotebookEdit: failed to write '{rawPath}': {ex.Message}");
        }

        return ToolResult.Success(summary);
    }

    private static string ApplyReplace(JsonArray cells, string cellId, string? cellType, string newSource)
    {
        var idx = FindIndexById(cells, cellId);
        if (idx < 0)
            throw new NotebookEditException($"cell with id '{cellId}' not found.");

        if (cells[idx] is not JsonObject cell)
            throw new NotebookEditException($"cell at index {idx} is not an object.");

        if (cellType is not null)
        {
            // Switching cell_type requires resetting the auxiliary fields the new type expects;
            // the old cell's outputs/execution_count would be meaningless on a markdown cell.
            cell["cell_type"] = cellType;
            if (cellType == "code")
            {
                cell["execution_count"] ??= null;
                cell["outputs"] ??= new JsonArray();
            }
            else
            {
                cell.Remove("execution_count");
                cell.Remove("outputs");
            }
        }

        cell["source"] = SourceToJsonArray(newSource);
        return $"Replaced source of cell '{cellId}'.";
    }

    private static string ApplyInsert(JsonArray cells, string? afterCellId, string cellType, string newSource)
    {
        int insertAt;
        if (string.IsNullOrEmpty(afterCellId))
        {
            insertAt = 0;
        }
        else
        {
            var idx = FindIndexById(cells, afterCellId);
            if (idx < 0)
                throw new NotebookEditException($"cell with id '{afterCellId}' not found.");
            insertAt = idx + 1;
        }

        var newId = NewCellId();
        var newCell = new JsonObject
        {
            ["cell_type"] = cellType,
            ["id"] = newId,
            ["metadata"] = new JsonObject(),
            ["source"] = SourceToJsonArray(newSource),
        };
        if (cellType == "code")
        {
            newCell["execution_count"] = null;
            newCell["outputs"] = new JsonArray();
        }

        cells.Insert(insertAt, newCell);
        return string.IsNullOrEmpty(afterCellId)
            ? $"Inserted new {cellType} cell at index 0 (id={newId})."
            : $"Inserted new {cellType} cell after '{afterCellId}' (id={newId}).";
    }

    private static string ApplyDelete(JsonArray cells, string cellId)
    {
        var idx = FindIndexById(cells, cellId);
        if (idx < 0)
            throw new NotebookEditException($"cell with id '{cellId}' not found.");

        cells.RemoveAt(idx);
        return $"Deleted cell '{cellId}'.";
    }

    private static int FindIndexById(JsonArray cells, string id)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i] is JsonObject cell
                && cell["id"]?.GetValue<string>() == id)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Convert plain source text to the canonical Jupyter array-of-strings form. Each line
    /// keeps its trailing \n EXCEPT the last line, which has no trailing \n unless the input
    /// ended with one. This mirrors what <c>nbformat</c>'s round-tripper produces and what
    /// VS Code / JupyterLab emit when they save a notebook.
    /// </summary>
    internal static JsonArray SourceToJsonArray(string source)
    {
        var array = new JsonArray();
        if (source.Length == 0) return array;

        var sb = new StringBuilder();
        for (var i = 0; i < source.Length; i++)
        {
            sb.Append(source[i]);
            if (source[i] == '\n')
            {
                array.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            array.Add(sb.ToString());
        return array;
    }

    private static string NewCellId() =>
        // 8 hex chars is what Jupyter's `nbformat` generator uses by default. Long enough
        // to avoid collisions in any real notebook (~3.4B), short enough not to bloat diffs.
        Guid.NewGuid().ToString("N").Substring(0, 8);

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private sealed class NotebookEditException : Exception
    {
        public NotebookEditException(string message) : base(message) { }
    }
}
