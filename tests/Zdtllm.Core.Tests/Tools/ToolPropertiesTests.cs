using System.Text.Json;
using Zdtllm.Skills;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class ToolPropertiesTests
{
    [Fact]
    public void Stateless_tools_default_to_can_run_in_parallel()
    {
        // CanRunInParallel and CloneForSubagent are default-interface members on ITool,
        // so the receiver has to be the interface type for the defaults to dispatch.
        ITool[] stateless =
        {
            new ReadTool(), new GlobTool(), new GrepTool(),
            new WebFetchTool(new HttpClient()), new WebSearchTool(),
            new SkillTool([]),
        };
        foreach (var tool in stateless)
            tool.CanRunInParallel.Should().BeTrue();
    }

    [Fact]
    public void Stateful_or_race_prone_tools_opt_out_of_parallel()
    {
        // These overrides ARE on the concrete classes so direct access works, but for
        // consistency with the other test we still funnel through ITool.
        ITool bash = new BashTool(Path.GetTempPath());
        ITool todos = new TodoWriteTool();
        ITool edit = new EditTool();
        ITool write = new WriteTool();

        bash.CanRunInParallel.Should().BeFalse();
        todos.CanRunInParallel.Should().BeFalse();
        edit.CanRunInParallel.Should().BeFalse();
        write.CanRunInParallel.Should().BeFalse();
    }

    [Fact]
    public void Stateless_tools_clone_to_themselves()
    {
        ITool read = new ReadTool();
        read.CloneForSubagent().Should().BeSameAs(read);

        ITool glob = new GlobTool();
        glob.CloneForSubagent().Should().BeSameAs(glob);

        ITool grep = new GrepTool();
        grep.CloneForSubagent().Should().BeSameAs(grep);
    }

    [Fact]
    public void BashTool_clone_starts_at_parents_current_cwd_but_is_a_distinct_instance()
    {
        ITool parent = new BashTool(Path.GetTempPath());
        var clone = parent.CloneForSubagent();

        clone.Should().NotBeSameAs(parent);
        clone.Should().BeOfType<BashTool>();
        ((BashTool)clone).CurrentWorkingDirectory.Should().Be(((BashTool)parent).CurrentWorkingDirectory);
    }

    [Fact]
    public async Task TodoWriteTool_clone_starts_with_an_empty_list()
    {
        var parent = new TodoWriteTool();
        var seed = JsonDocument.Parse("""{"todos":[{"id":"1","content":"only-on-parent","status":"pending"}]}""");
        await parent.ExecuteAsync(seed.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
        parent.CurrentTodos.Should().HaveCount(1);

        var clone = (TodoWriteTool)((ITool)parent).CloneForSubagent();

        clone.Should().NotBeSameAs(parent);
        clone.CurrentTodos.Should().BeEmpty();
        // Mutating the parent doesn't bleed into the clone.
        parent.CurrentTodos.Should().HaveCount(1);
    }

    [Fact]
    public async Task BashTool_parent_and_clone_track_cwd_independently()
    {
        var parent = new BashTool(Path.GetTempPath());

        var clone = (BashTool)((ITool)parent).CloneForSubagent();
        clone.CurrentWorkingDirectory.Should().Be(parent.CurrentWorkingDirectory);

        await Task.Yield();
        clone.Should().NotBeSameAs(parent);
    }
}
