using System.Text.Json;

namespace Zdtllm.Core.Workflows;

/// <summary>
/// Reads workflow definitions from <c>{cwd}/.zdtllm/workflows/*.json</c>. Provides listing (for
/// <c>/workflows</c> and error messages) and load-by-name with validation. Malformed files are
/// skipped when listing but throw a clear <see cref="WorkflowException"/> when loaded by name.
/// </summary>
public sealed class WorkflowLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _dir;

    public WorkflowLoader(string cwd)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd);
        _dir = Path.Combine(cwd, ".zdtllm", "workflows");
    }

    public string Directory => _dir;

    /// <summary>Summaries of every valid workflow file, ordered by name. Empty if the dir is absent.</summary>
    public IReadOnlyList<WorkflowSummary> List()
    {
        if (!System.IO.Directory.Exists(_dir)) return Array.Empty<WorkflowSummary>();
        var result = new List<WorkflowSummary>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var raw = JsonSerializer.Deserialize<RawWorkflow>(File.ReadAllText(file), JsonOpts);
                var name = string.IsNullOrWhiteSpace(raw?.Name)
                    ? Path.GetFileNameWithoutExtension(file)
                    : raw!.Name!;
                result.Add(new WorkflowSummary(name, raw?.Description, raw?.Phases?.Count ?? 0));
            }
            catch (JsonException) { /* skip malformed files in a listing */ }
        }
        return result;
    }

    /// <summary>
    /// Load and validate the workflow named <paramref name="name"/> (with or without the .json
    /// extension). Throws <see cref="WorkflowException"/> if the file is missing or invalid.
    /// </summary>
    public WorkflowDefinition Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var file = ResolveFile(name);
        if (file is null)
        {
            var available = List();
            var hint = available.Count == 0
                ? $"No workflows found in {_dir}."
                : "Available: " + string.Join(", ", available.Select(w => w.Name)) + ".";
            throw new WorkflowException($"Workflow '{name}' not found. {hint}");
        }

        RawWorkflow? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawWorkflow>(File.ReadAllText(file), JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new WorkflowException($"Workflow '{name}' is not valid JSON: {ex.Message}", ex);
        }

        return Validate(raw, Path.GetFileNameWithoutExtension(file));
    }

    private string? ResolveFile(string name)
    {
        var bare = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? name[..^5]
            : name;
        var path = Path.Combine(_dir, bare + ".json");
        return File.Exists(path) ? path : null;
    }

    private static WorkflowDefinition Validate(RawWorkflow? raw, string fallbackName)
    {
        if (raw is null) throw new WorkflowException("Workflow file was empty.");

        var name = string.IsNullOrWhiteSpace(raw.Name) ? fallbackName : raw.Name!;
        if (raw.Phases is null || raw.Phases.Count == 0)
            throw new WorkflowException($"Workflow '{name}' has no phases.");

        var phases = new List<WorkflowPhase>(raw.Phases.Count);
        var idx = 0;
        foreach (var p in raw.Phases)
        {
            idx++;
            var title = string.IsNullOrWhiteSpace(p.Title) ? $"phase{idx}" : p.Title!.Trim();
            if (string.IsNullOrWhiteSpace(p.Prompt))
                throw new WorkflowException($"Workflow '{name}': phase '{title}' is missing a 'prompt'.");

            var agent = string.IsNullOrWhiteSpace(p.Agent) ? "general-purpose" : p.Agent!.Trim();
            // Fan-out phases default to parallel; single-run phases don't care.
            var parallel = p.Parallel ?? !string.IsNullOrWhiteSpace(p.ForEach);
            var maxTurns = p.MaxTurns is > 0 ? p.MaxTurns.Value : 25;

            phases.Add(new WorkflowPhase(
                Title: title,
                Agent: agent,
                Prompt: p.Prompt!,
                ForEach: string.IsNullOrWhiteSpace(p.ForEach) ? null : p.ForEach!.Trim(),
                Parallel: parallel,
                MaxTurns: maxTurns));
        }

        return new WorkflowDefinition(
            Name: name,
            Description: raw.Description,
            Inputs: raw.Inputs is { Count: > 0 } ? raw.Inputs : Array.Empty<string>(),
            Phases: phases);
    }

    private sealed class RawWorkflow
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Inputs { get; set; }
        public List<RawPhase>? Phases { get; set; }
    }

    private sealed class RawPhase
    {
        public string? Title { get; set; }
        public string? Agent { get; set; }
        public string? Prompt { get; set; }
        public string? ForEach { get; set; }
        public bool? Parallel { get; set; }
        public int? MaxTurns { get; set; }
    }
}
