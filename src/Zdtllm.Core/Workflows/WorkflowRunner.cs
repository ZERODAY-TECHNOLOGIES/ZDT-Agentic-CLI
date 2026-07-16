using Zdtllm.Tools;

namespace Zdtllm.Core.Workflows;

/// <summary>
/// Executes a <see cref="WorkflowDefinition"/> deterministically on top of the existing subagent
/// machinery. Phases run in order; each phase either dispatches a single subagent or fans one out
/// over an input list (in parallel, capped by <c>maxParallel</c>, or sequentially). Every phase's
/// combined output is exposed to later phases as <c>{{Title.results}}</c>. A step that throws is
/// recorded as an error string rather than aborting the whole run — one flaky subagent shouldn't
/// sink the workflow — but genuine cancellation propagates.
/// </summary>
public sealed class WorkflowRunner
{
    private readonly ISubagentRunner _runner;

    public WorkflowRunner(ISubagentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<WorkflowResult> RunAsync(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, string> args,
        TextWriter status,
        CancellationToken ct = default,
        int maxParallel = 0,
        string? parentModel = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(status);

        var context = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in args) context[kv.Key] = kv.Value;

        var phaseResults = new List<WorkflowPhaseResult>(workflow.Phases.Count);
        var pi = 0;
        foreach (var phase in workflow.Phases)
        {
            ct.ThrowIfCancellationRequested();
            pi++;
            await status.WriteLineAsync(
                $"▶ phase {pi}/{workflow.Phases.Count}: {phase.Title}").ConfigureAwait(false);

            List<string> outputs;
            if (phase.ForEach is not null)
            {
                var items = SplitList(context.GetValueOrDefault(phase.ForEach, string.Empty));
                if (items.Count == 0)
                {
                    await status.WriteLineAsync(
                        $"  (skipped — input '{phase.ForEach}' is empty; nothing to fan out over)")
                        .ConfigureAwait(false);
                    outputs = new List<string>();
                }
                else
                {
                    await status.WriteLineAsync(
                        $"  ↳ fan-out over {items.Count} item(s){(phase.Parallel ? " (parallel)" : " (sequential)")}")
                        .ConfigureAwait(false);
                    outputs = await RunFanOutAsync(phase, items, context, maxParallel, parentModel, ct)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                var prompt = WorkflowTemplate.Resolve(phase.Prompt, context);
                outputs = new List<string> { await RunStepAsync(phase, prompt, parentModel, ct).ConfigureAwait(false) };
            }

            context[$"{phase.Title}.results"] = string.Join("\n\n---\n\n", outputs);
            phaseResults.Add(new WorkflowPhaseResult(phase.Title, outputs));
        }

        var final = phaseResults.Count > 0
            ? string.Join("\n\n---\n\n", phaseResults[^1].Outputs)
            : string.Empty;
        return new WorkflowResult(workflow.Name, phaseResults, final);
    }

    private async Task<List<string>> RunFanOutAsync(
        WorkflowPhase phase,
        IReadOnlyList<string> items,
        IReadOnlyDictionary<string, string> baseContext,
        int maxParallel,
        string? parentModel,
        CancellationToken ct)
    {
        if (!phase.Parallel)
        {
            var seq = new List<string>(items.Count);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var prompt = WorkflowTemplate.Resolve(phase.Prompt, WithItem(baseContext, item));
                seq.Add(await RunStepAsync(phase, prompt, parentModel, ct, item).ConfigureAwait(false));
            }
            return seq;
        }

        using var sem = maxParallel > 0 && maxParallel < items.Count
            ? new SemaphoreSlim(maxParallel)
            : null;

        var tasks = items.Select(item =>
        {
            var prompt = WorkflowTemplate.Resolve(phase.Prompt, WithItem(baseContext, item));
            return RunStepGuardedAsync(sem, phase, prompt, parentModel, item, ct);
        }).ToArray();

        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    private async Task<string> RunStepGuardedAsync(
        SemaphoreSlim? sem, WorkflowPhase phase, string prompt, string? parentModel, string? item, CancellationToken ct)
    {
        if (sem is null) return await RunStepAsync(phase, prompt, parentModel, ct, item).ConfigureAwait(false);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { return await RunStepAsync(phase, prompt, parentModel, ct, item).ConfigureAwait(false); }
        finally { sem.Release(); }
    }

    private async Task<string> RunStepAsync(
        WorkflowPhase phase, string prompt, string? parentModel, CancellationToken ct, string? item = null)
    {
        // Include the fan-out item in the description so the live-activity tag distinguishes
        // parallel agents (e.g. "Review: a.cs" vs "Review: b.cs").
        var description = string.IsNullOrEmpty(item) ? phase.Title : $"{phase.Title}: {item}";
        var request = new SubagentRequest(
            Description: description,
            Prompt: prompt,
            Type: phase.Agent,
            MaxTurns: phase.MaxTurns,
            ParentModel: parentModel);
        try
        {
            var result = await _runner.RunAsync(request, ct).ConfigureAwait(false);
            return result.FinalText;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real cancellation aborts the whole workflow
        }
        catch (Exception ex)
        {
            // A single failing subagent is recorded, not fatal — later phases still run.
            return $"[step '{phase.Title}' failed: {ex.Message}]";
        }
    }

    private static Dictionary<string, string> WithItem(IReadOnlyDictionary<string, string> ctx, string item)
    {
        var copy = new Dictionary<string, string>(ctx, StringComparer.Ordinal) { ["item"] = item };
        return copy;
    }

    /// <summary>Split a fan-out input into items on commas or newlines, trimming blanks.</summary>
    internal static IReadOnlyList<string> SplitList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
