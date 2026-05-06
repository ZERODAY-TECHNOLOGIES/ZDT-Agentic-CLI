using System.Text.Json;
using System.Text.Json.Nodes;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class NotebookEditToolTests : IDisposable
{
    private readonly string _tempDir;

    public NotebookEditToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-nbedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Builds a minimal notebook with two cells (a markdown header and a code cell).
    /// Returns the path. Each cell has a stable id we assert against.
    /// </summary>
    private string MakeNotebook(string fileName = "n.ipynb")
    {
        var nb = new JsonObject
        {
            ["nbformat"] = 4,
            ["nbformat_minor"] = 5,
            ["metadata"] = new JsonObject(),
            ["cells"] = new JsonArray
            {
                new JsonObject
                {
                    ["cell_type"] = "markdown",
                    ["id"] = "m1",
                    ["metadata"] = new JsonObject(),
                    ["source"] = new JsonArray { "# Title\n", "intro paragraph" },
                },
                new JsonObject
                {
                    ["cell_type"] = "code",
                    ["id"] = "c1",
                    ["metadata"] = new JsonObject(),
                    ["execution_count"] = null,
                    ["outputs"] = new JsonArray(),
                    ["source"] = new JsonArray { "print(1)\n" },
                },
            },
        };
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, nb.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static async Task<ToolResult> RunAsync(string cwd, object args)
    {
        var tool = new NotebookEditTool();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(cwd), CancellationToken.None);
    }

    private static JsonObject Reload(string path) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public async Task Replace_overwrites_source_and_preserves_cell_type_when_unspecified()
    {
        var path = MakeNotebook();

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "c1",
            new_source = "print(2)\nprint(3)\n",
        });

        result.IsError.Should().BeFalse();
        var nb = Reload(path);
        var cell = nb["cells"]![1]!.AsObject();
        cell["cell_type"]!.GetValue<string>().Should().Be("code");
        cell["source"]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("print(2)\n", "print(3)\n");
        // Code-cell auxiliary fields preserved.
        cell["outputs"]!.AsArray().Should().BeEmpty();
        cell.ContainsKey("execution_count").Should().BeTrue();
    }

    [Fact]
    public async Task Replace_can_change_cell_type_from_code_to_markdown_and_drops_outputs()
    {
        var path = MakeNotebook();

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "c1",
            cell_type = "markdown",
            new_source = "now markdown",
        });

        result.IsError.Should().BeFalse();
        var cell = Reload(path)["cells"]![1]!.AsObject();
        cell["cell_type"]!.GetValue<string>().Should().Be("markdown");
        cell.ContainsKey("outputs").Should().BeFalse();
        cell.ContainsKey("execution_count").Should().BeFalse();
    }

    [Fact]
    public async Task Insert_adds_after_named_cell()
    {
        var path = MakeNotebook();

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "m1",
            edit_mode = "insert",
            cell_type = "code",
            new_source = "x = 42",
        });

        result.IsError.Should().BeFalse();
        var cells = Reload(path)["cells"]!.AsArray();
        cells.Count.Should().Be(3);
        cells[0]!["id"]!.GetValue<string>().Should().Be("m1");
        cells[1]!["cell_type"]!.GetValue<string>().Should().Be("code");
        cells[1]!["source"]!.AsArray()[0]!.GetValue<string>().Should().Be("x = 42");
        // execution_count is present and serialized as JSON null. In JsonNode, a "key with
        // null value" surfaces as a null indexer return — assert via ContainsKey + null value.
        cells[1]!.AsObject().ContainsKey("execution_count").Should().BeTrue();
        cells[1]!["execution_count"].Should().BeNull();
        cells[1]!["id"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        cells[2]!["id"]!.GetValue<string>().Should().Be("c1");
    }

    [Fact]
    public async Task Insert_at_start_when_cell_id_omitted()
    {
        var path = MakeNotebook();

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            edit_mode = "insert",
            cell_type = "markdown",
            new_source = "preface",
        });

        result.IsError.Should().BeFalse();
        var cells = Reload(path)["cells"]!.AsArray();
        cells.Count.Should().Be(3);
        cells[0]!["cell_type"]!.GetValue<string>().Should().Be("markdown");
        cells[0]!["source"]!.AsArray()[0]!.GetValue<string>().Should().Be("preface");
        cells[1]!["id"]!.GetValue<string>().Should().Be("m1");
    }

    [Fact]
    public async Task Delete_removes_named_cell()
    {
        var path = MakeNotebook();

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "c1",
            edit_mode = "delete",
        });

        result.IsError.Should().BeFalse();
        var cells = Reload(path)["cells"]!.AsArray();
        cells.Count.Should().Be(1);
        cells[0]!["id"]!.GetValue<string>().Should().Be("m1");
    }

    [Fact]
    public async Task Errors_when_cell_id_not_found()
    {
        MakeNotebook();

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "does-not-exist",
            new_source = "anything",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("does-not-exist");
    }

    [Fact]
    public async Task Errors_when_notebook_path_missing()
    {
        var result = await RunAsync(_tempDir, new { cell_id = "x", new_source = "y" });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("notebook_path");
    }

    [Fact]
    public async Task Errors_on_invalid_edit_mode()
    {
        MakeNotebook();
        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "c1",
            edit_mode = "bogus",
            new_source = "x",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("edit_mode");
    }

    [Fact]
    public async Task Errors_on_invalid_cell_type()
    {
        MakeNotebook();
        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            edit_mode = "insert",
            cell_type = "raw",
            new_source = "x",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("cell_type");
    }

    [Fact]
    public async Task Errors_on_malformed_notebook()
    {
        var path = Path.Combine(_tempDir, "bad.ipynb");
        await File.WriteAllTextAsync(path, "{not valid json}");

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "bad.ipynb",
            cell_id = "x",
            new_source = "y",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("parse");
    }

    [Fact]
    public async Task Errors_when_notebook_missing_cells_array()
    {
        var path = Path.Combine(_tempDir, "no-cells.ipynb");
        await File.WriteAllTextAsync(path, """{"nbformat":4}""");

        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "no-cells.ipynb",
            cell_id = "x",
            new_source = "y",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("cells");
    }

    [Fact]
    public async Task Insert_requires_cell_type()
    {
        MakeNotebook();
        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            edit_mode = "insert",
            new_source = "x",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("cell_type");
    }

    [Fact]
    public async Task Replace_requires_cell_id()
    {
        MakeNotebook();
        var result = await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            new_source = "x",
        });

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("cell_id");
    }

    [Fact]
    public async Task Top_level_metadata_and_nbformat_are_preserved()
    {
        var path = MakeNotebook();

        await RunAsync(_tempDir, new
        {
            notebook_path = "n.ipynb",
            cell_id = "c1",
            new_source = "y = 1",
        });

        var nb = Reload(path);
        nb["nbformat"]!.GetValue<int>().Should().Be(4);
        nb["nbformat_minor"]!.GetValue<int>().Should().Be(5);
        nb["metadata"].Should().NotBeNull();
    }

    [Fact]
    public void Source_to_array_splits_on_newlines_keeping_trailing_n_per_line_except_last()
    {
        var arr = NotebookEditTool.SourceToJsonArray("a\nb\nc");
        arr.Select(n => n!.GetValue<string>()).Should().Equal("a\n", "b\n", "c");

        var arrTrailing = NotebookEditTool.SourceToJsonArray("a\nb\n");
        arrTrailing.Select(n => n!.GetValue<string>()).Should().Equal("a\n", "b\n");

        var arrEmpty = NotebookEditTool.SourceToJsonArray("");
        arrEmpty.Should().BeEmpty();
    }
}
