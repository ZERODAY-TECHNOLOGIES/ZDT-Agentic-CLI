using Zdtllm.Core.Workflows;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Workflows;

public sealed class WorkflowRunnerTests
{
    /// <summary>Records every subagent request and returns a scripted (or throwing) response.</summary>
    private sealed class FakeRunner : ISubagentRunner
    {
        private readonly object _lock = new();
        public List<SubagentRequest> Requests { get; } = new();
        public Func<SubagentRequest, string> Responder { get; set; } = r => $"out:{r.Prompt}";

        public Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct)
        {
            lock (_lock) Requests.Add(request);
            var text = Responder(request); // may throw to simulate a failing step
            return Task.FromResult(new SubagentResult(text, 1, null, null, request.Type));
        }

        public bool SupportsType(string type) => true;
        public IReadOnlyList<string> AvailableTypes => new[] { "general-purpose" };
        public IReadOnlyList<SubagentTypeInfo> GetTypeInfo() => Array.Empty<SubagentTypeInfo>();
    }

    private static WorkflowPhase Phase(string title, string prompt, string? forEach = null,
        bool parallel = false, string agent = "general-purpose", int maxTurns = 25) =>
        new(title, agent, prompt, forEach, parallel, maxTurns);

    private static WorkflowDefinition Wf(params WorkflowPhase[] phases) =>
        new("wf", null, Array.Empty<string>(), phases);

    private static Task<WorkflowResult> RunAsync(FakeRunner runner, WorkflowDefinition wf,
        IReadOnlyDictionary<string, string>? args = null) =>
        new WorkflowRunner(runner).RunAsync(wf, args ?? new Dictionary<string, string>(), TextWriter.Null);

    [Fact]
    public async Task Single_phase_runs_one_subagent_and_captures_output()
    {
        var runner = new FakeRunner { Responder = _ => "the answer" };
        var result = await RunAsync(runner, Wf(Phase("Solve", "do the thing")));

        runner.Requests.Should().ContainSingle();
        runner.Requests[0].Prompt.Should().Be("do the thing");
        runner.Requests[0].Description.Should().Be("Solve");
        result.FinalOutput.Should().Be("the answer");
        result.Phases.Single().Outputs.Should().Equal("the answer");
    }

    [Fact]
    public async Task ForEach_fans_out_one_subagent_per_item_with_item_templating()
    {
        var runner = new FakeRunner { Responder = r => r.Prompt.ToUpperInvariant() };
        var args = new Dictionary<string, string> { ["files"] = "a.cs, b.cs , c.cs" };

        var result = await RunAsync(runner,
            Wf(Phase("Review", "review {{item}}", forEach: "files", parallel: false)), args);

        runner.Requests.Select(r => r.Prompt)
            .Should().Equal("review a.cs", "review b.cs", "review c.cs"); // trimmed + ordered (sequential)
        result.Phases[0].Outputs.Should().Equal("REVIEW A.CS", "REVIEW B.CS", "REVIEW C.CS");
    }

    [Fact]
    public async Task Prior_phase_results_feed_the_next_phase()
    {
        var runner = new FakeRunner
        {
            Responder = r => r.Description.StartsWith("Review") ? $"finding({r.Prompt})" : "SYNTH:" + r.Prompt,
        };
        var args = new Dictionary<string, string> { ["files"] = "x,y" };

        var result = await RunAsync(runner, Wf(
            Phase("Review", "check {{item}}", forEach: "files", parallel: false),
            Phase("Synthesize", "combine: {{Review.results}}")), args);

        // The synth step's prompt must contain both review outputs.
        var synthReq = runner.Requests.Last();
        synthReq.Prompt.Should().Contain("finding(check x)");
        synthReq.Prompt.Should().Contain("finding(check y)");
        result.FinalOutput.Should().StartWith("SYNTH:");
    }

    [Fact]
    public async Task Arg_placeholders_are_substituted()
    {
        var runner = new FakeRunner();
        var args = new Dictionary<string, string> { ["topic"] = "quantum widgets" };

        await RunAsync(runner, Wf(Phase("Go", "research {{topic}} deeply")), args);

        runner.Requests[0].Prompt.Should().Be("research quantum widgets deeply");
    }

    [Fact]
    public async Task A_failing_step_is_recorded_and_the_workflow_continues()
    {
        var runner = new FakeRunner
        {
            Responder = r => r.Prompt.Contains("boom")
                ? throw new InvalidOperationException("kaboom")
                : "ok:" + r.Prompt,
        };
        var args = new Dictionary<string, string> { ["items"] = "fine, boom, alsofine" };

        var result = await RunAsync(runner, Wf(
            Phase("Work", "{{item}}", forEach: "items", parallel: false),
            Phase("After", "still ran")), args);

        result.Phases[0].Outputs.Should().HaveCount(3);
        result.Phases[0].Outputs[1].Should().Contain("failed").And.Contain("kaboom");
        // Later phase still executed despite the mid-phase failure.
        result.Phases[1].Outputs.Should().ContainSingle();
        result.FinalOutput.Should().Be("ok:still ran");
    }

    [Fact]
    public async Task Empty_forEach_input_skips_the_phase()
    {
        var runner = new FakeRunner();
        var result = await RunAsync(runner,
            Wf(Phase("Review", "review {{item}}", forEach: "files")),
            new Dictionary<string, string> { ["files"] = "  " });

        runner.Requests.Should().BeEmpty();
        result.Phases[0].Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task Parallel_fan_out_runs_every_item()
    {
        var runner = new FakeRunner { Responder = r => r.Prompt };
        var args = new Dictionary<string, string> { ["n"] = "1,2,3,4,5" };

        var result = await new WorkflowRunner(runner).RunAsync(
            Wf(Phase("P", "{{item}}", forEach: "n", parallel: true)),
            args, TextWriter.Null, CancellationToken.None, maxParallel: 2);

        runner.Requests.Should().HaveCount(5);
        result.Phases[0].Outputs.Should().BeEquivalentTo(new[] { "1", "2", "3", "4", "5" });
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var runner = new FakeRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await new WorkflowRunner(runner).RunAsync(
            Wf(Phase("P", "x")), new Dictionary<string, string>(), TextWriter.Null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        runner.Requests.Should().BeEmpty(); // cancelled before dispatching
    }
}
