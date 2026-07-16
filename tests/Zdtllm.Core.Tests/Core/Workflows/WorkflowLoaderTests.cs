using Zdtllm.Core.Workflows;

namespace Zdtllm.Core.Tests.Core.Workflows;

public sealed class WorkflowLoaderTests : IDisposable
{
    private readonly string _cwd;
    private readonly string _wfDir;

    public WorkflowLoaderTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), "zdt-wf-" + Guid.NewGuid().ToString("N"));
        _wfDir = Path.Combine(_cwd, ".zdtllm", "workflows");
        Directory.CreateDirectory(_wfDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, recursive: true); }
        catch { /* best effort */ }
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_wfDir, name + ".json"), json);

    [Fact]
    public void Loads_and_applies_defaults()
    {
        Write("review", """
            {
              "name": "review",
              "description": "review then synth",
              "inputs": ["files"],
              "phases": [
                { "title": "Review", "prompt": "look at {{item}}", "forEach": "files" },
                { "title": "Synthesize", "agent": "general-purpose", "prompt": "sum {{Review.results}}", "maxTurns": 40 }
              ]
            }
            """);

        var wf = new WorkflowLoader(_cwd).Load("review");

        wf.Name.Should().Be("review");
        wf.Phases.Should().HaveCount(2);

        var p0 = wf.Phases[0];
        p0.Title.Should().Be("Review");
        p0.Agent.Should().Be("general-purpose"); // default
        p0.ForEach.Should().Be("files");
        p0.Parallel.Should().BeTrue();            // fan-out defaults to parallel
        p0.MaxTurns.Should().Be(25);              // default

        wf.Phases[1].MaxTurns.Should().Be(40);
        wf.Phases[1].ForEach.Should().BeNull();
        wf.Phases[1].Parallel.Should().BeFalse(); // single-run phase defaults non-parallel
    }

    [Fact]
    public void Load_accepts_name_with_or_without_json_extension()
    {
        Write("x", """{ "phases": [ { "title": "P", "prompt": "hi" } ] }""");

        new WorkflowLoader(_cwd).Load("x").Name.Should().Be("x");
        new WorkflowLoader(_cwd).Load("x.json").Name.Should().Be("x");
    }

    [Fact]
    public void Missing_workflow_throws_with_available_list()
    {
        Write("alpha", """{ "phases": [ { "title": "P", "prompt": "hi" } ] }""");

        var act = () => new WorkflowLoader(_cwd).Load("nope");
        act.Should().Throw<WorkflowException>().WithMessage("*not found*alpha*");
    }

    [Fact]
    public void No_phases_is_a_validation_error()
    {
        Write("empty", """{ "name": "empty", "phases": [] }""");

        var act = () => new WorkflowLoader(_cwd).Load("empty");
        act.Should().Throw<WorkflowException>().WithMessage("*no phases*");
    }

    [Fact]
    public void Phase_without_prompt_is_a_validation_error()
    {
        Write("bad", """{ "phases": [ { "title": "P" } ] }""");

        var act = () => new WorkflowLoader(_cwd).Load("bad");
        act.Should().Throw<WorkflowException>().WithMessage("*missing a 'prompt'*");
    }

    [Fact]
    public void Invalid_json_throws_clear_error()
    {
        Write("broken", "{ not json ]");

        var act = () => new WorkflowLoader(_cwd).Load("broken");
        act.Should().Throw<WorkflowException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void List_summarizes_and_skips_malformed()
    {
        Write("one", """{ "name": "one", "description": "first", "phases": [ { "title": "P", "prompt": "x" } ] }""");
        Write("two", """{ "phases": [ { "title": "A", "prompt": "x" }, { "title": "B", "prompt": "y" } ] }""");
        Write("junk", "not json at all");

        var list = new WorkflowLoader(_cwd).List();

        list.Should().HaveCount(2); // the unparseable "junk" file is skipped
        list.Select(w => w.Name).Should().NotContain("junk");
        list.Single(w => w.Name == "one").Description.Should().Be("first");
        list.Single(w => w.Name == "two").PhaseCount.Should().Be(2);
    }

    [Fact]
    public void List_is_empty_when_directory_absent()
    {
        var other = Path.Combine(Path.GetTempPath(), "zdt-none-" + Guid.NewGuid().ToString("N"));
        new WorkflowLoader(other).List().Should().BeEmpty();
    }
}
