using Zdtllm.Core.Sessions;
using Zdtllm.Tools;

namespace Zdtllm.Core;

/// <summary>
/// Real subagents. Each call spins up a brand-new AgentLoop with:
///   - the parent's LiteLLM client, model, mode, permission rules
///   - a constrained tool registry per subagent_type (so a code-reviewer
///     literally cannot Write or Bash)
///   - a focused system prompt that replaces the parent's bloated one
///   - an ephemeral session (no JSONL persistence — subagents are
///     transient by design)
/// The subagent's intermediate tool calls and reasoning never reach the
/// parent — only its final assistant text. That's the point: the
/// parent's context stays clean; the subagent gets a fresh perspective.
/// </summary>
public sealed class SubagentRunner : ISubagentRunner
{
    private static readonly Dictionary<string, IReadOnlySet<string>> ToolPolicyByType =
        new(StringComparer.Ordinal)
        {
            ["code-reviewer"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "Read", "Glob", "Grep", "TodoWrite",
            },
            ["explore"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "Read", "Glob", "Grep", "WebFetch", "TodoWrite",
            },
            // "general-purpose" → null → all parent tools EXCEPT Task itself
        };

    private static readonly string[] AvailableTypeNames =
    {
        "general-purpose",
        "code-reviewer",
        "explore",
    };

    private static readonly Dictionary<string, string> TypeBlurbs =
        new(StringComparer.Ordinal)
        {
            ["general-purpose"] = "All tools the parent has, except Task itself (no recursive sub-spawning).",
            ["code-reviewer"]   = "Read-only review profile — analyses code without ever modifying it.",
            ["explore"]         = "Read-only research profile — local FS plus web fetch for sourced answers.",
        };

    private readonly AgentLoop _parent;

    /// <summary>
    /// Optional shared sink for live subagent activity. When set, every subagent's output + status
    /// is streamed here line-by-line, each line tagged with the agent's label, so several parallel
    /// subagents' work is visible interleaved in one stream. Null (tests, quiet -p) keeps the old
    /// behaviour: subagent chatter is buffered and discarded, only the final text bubbles up.
    /// </summary>
    private readonly TextWriter? _activitySink;
    private readonly object _sinkLock = new();
    private int _dispatchCounter;

    /// <summary>
    /// Optional interactive fleet view. When set, each subagent is registered with it and its
    /// output/status is fed there line-by-line so the user can navigate between live agents.
    /// Takes precedence over <see cref="_activitySink"/>; null keeps the plain (sink/buffer) path.
    /// </summary>
    private readonly AgentFleet.IAgentFleetMonitor? _fleetMonitor;

    public SubagentRunner(
        AgentLoop parent,
        TextWriter? activitySink = null,
        AgentFleet.IAgentFleetMonitor? fleetMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _parent = parent;
        _activitySink = activitySink;
        _fleetMonitor = fleetMonitor;
    }

    public IReadOnlyList<string> AvailableTypes => AvailableTypeNames;

    public bool SupportsType(string type) =>
        AvailableTypeNames.Contains(type, StringComparer.Ordinal);

    public IReadOnlyList<SubagentTypeInfo> GetTypeInfo()
    {
        var infos = new List<SubagentTypeInfo>(AvailableTypeNames.Length);
        foreach (var type in AvailableTypeNames)
        {
            var allowed = ToolPolicyByType.TryGetValue(type, out var set)
                ? (IReadOnlyList<string>)set.OrderBy(n => n, StringComparer.Ordinal).ToList()
                : new[] { "*" };
            var blurb = TypeBlurbs.TryGetValue(type, out var b) ? b : string.Empty;
            infos.Add(new SubagentTypeInfo(type, blurb, allowed));
        }
        return infos;
    }

    public async Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One stable label per dispatch, used to tag this subagent's live activity so parallel
        // agents stay distinguishable in the shared stream. Uses the description (the model's /
        // workflow's short title) plus a monotonic number.
        var label = BuildLabel(request.Description, Interlocked.Increment(ref _dispatchCounter));

        // Register the agent with the fleet view (if any) up front so it appears the moment it
        // starts, and mark it done/failed exactly once at the end (across all retry attempts).
        var fleetId = _fleetMonitor?.Register(label) ?? -1;
        var succeeded = false;
        try
        {
            // Attempt 1: requested type. Attempt 2: same type (transient retry — most LiteLLM/network
            // failures are flaky one-shot timeouts). Attempt 3 (only if requested type wasn't already
            // general-purpose): fall back to general-purpose, which has the broadest tool set and the
            // simplest prompt — likeliest to succeed when a constrained profile keeps failing.
            var attempts = new List<(string Type, bool IsFallback)>
            {
                (request.Type, false),
                (request.Type, false),
            };
            if (!string.Equals(request.Type, "general-purpose", StringComparison.Ordinal))
                attempts.Add(("general-purpose", true));

            Exception? lastError = null;
            for (var i = 0; i < attempts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (effectiveType, isFallback) = attempts[i];
                try
                {
                    var result = await RunOnceAsync(request with { Type = effectiveType }, label, fleetId, ct).ConfigureAwait(false);
                    succeeded = true;
                    if (isFallback)
                    {
                        var note = $"[fallback to general-purpose after {i} failure(s) of '{request.Type}']\n\n";
                        return result with { FinalText = note + result.FinalText };
                    }
                    return result;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // User-requested cancellation — surface immediately, do not retry.
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new SubagentExecutionException(
                $"Subagent '{request.Type}' failed after {attempts.Count} attempt(s): {lastError?.Message}",
                lastError);
        }
        finally
        {
            _fleetMonitor?.Complete(fleetId, failed: !succeeded);
        }
    }

    /// <summary>Compact, unique-ish tag for a subagent's live-activity lines: <c>[desc #n]</c>.</summary>
    private static string BuildLabel(string description, int seq)
    {
        var desc = string.IsNullOrWhiteSpace(description) ? "agent" : description.Trim();
        if (desc.Length > 28) desc = string.Concat(desc.AsSpan(0, 28), "…");
        return $"[{desc} #{seq}] ";
    }

    private async Task<SubagentResult> RunOnceAsync(SubagentRequest request, string label, int fleetId, CancellationToken ct)
    {
        var subRegistry = BuildRegistryForType(request.Type, _parent.Tools);
        // Resolve which model the subagent runs on. Priority:
        //   1. request.OverrideModel — set by TaskTool when SubagentModelResolver picked a
        //      tiered model for the requested subagent_type (e.g. code-reviewer → light tier).
        //      This is how the user's litellm.subagentModels config reaches the AgentLoop.
        //   2. request.ParentModel — TaskTool plumbs the parent's CURRENT session model
        //      (i.e. whatever /model last set), so a mid-conversation model switch reaches
        //      subagents that don't have a tier override.
        //   3. _parent.Options.Model — the agent's startup-frozen option, used as fallback
        //      when the request didn't specify either of the above (e.g. tests that build a
        //      SubagentRequest directly without a TaskTool / ToolContext in front).
        var resolvedModel = !string.IsNullOrEmpty(request.OverrideModel)
            ? request.OverrideModel
            : !string.IsNullOrEmpty(request.ParentModel)
                ? request.ParentModel
                : _parent.Options.Model;
        var subOptions = _parent.Options with
        {
            Model = resolvedModel,
            SystemPrompt = SystemPromptForType(request.Type),
            MaxTurns = request.MaxTurns,
        };

        // Each subagent gets its OWN ContextManager mirroring the parent's settings —
        // so its threshold tracking and auto-compact behaviour are independent from
        // the parent's. Without this, a long-running subagent (e.g. a code-reviewer
        // crawling 50 files) would silently exhaust its window without ever auto-compacting.
        var subContext = _parent.Context is { } parentContext
            ? new ContextManager(
                parentContext.ContextWindow,
                parentContext.MediumModel,
                parentContext.SoftThreshold,
                parentContext.HardThreshold)
            : null;

        var subAgent = new AgentLoop(
            _parent.Client,
            subRegistry,
            _parent.Permissions,
            subOptions,
            context: subContext);

        using var session = Session.NewEphemeral(subOptions.Model, subOptions.ToolCallingMode);

        // Route this subagent's output + status to the best available display:
        //   1. the interactive fleet view (line-buffered into its per-agent panel), or
        //   2. a tagged stream sink (each line prefixed with the label), or
        //   3. a discard buffer (only the final text bubbles up).
        // The final text is returned from the AgentResult regardless, so nothing is lost.
        TextWriter output, status;
        if (_fleetMonitor is not null)
        {
            output = new LineBufferedWriter(line => _fleetMonitor.Append(fleetId, line));
            status = new LineBufferedWriter(line => _fleetMonitor.Append(fleetId, line));
        }
        else if (_activitySink is not null)
        {
            output = new LivePrefixWriter(_activitySink, _sinkLock, label);
            status = new LivePrefixWriter(_activitySink, _sinkLock, label);
        }
        else
        {
            output = new StringWriter();
            status = new StringWriter();
        }

        try
        {
            var result = await subAgent.RunTurnAsync(
                session,
                request.Prompt,
                output: output,
                status: status,
                ct: ct).ConfigureAwait(false);

            return new SubagentResult(
                FinalText: result.FinalText,
                Turns: result.Turns,
                PromptTokens: result.PromptTokens,
                CompletionTokens: result.CompletionTokens,
                Model: resolvedModel);
        }
        finally
        {
            output.Dispose();
            status.Dispose();
        }
    }

    /// <summary>
    /// Builds a registry for the requested type. The Agent tool itself is always excluded
    /// to prevent recursive sub-spawning — if a subagent could call Agent, you'd get
    /// fork-bombs at best and exploding bills at worst. Stateful tools (anything whose
    /// CloneForSubagent override returns a fresh instance) are isolated per subagent so
    /// parallel subagents don't race on shared mutable state (Bash's cwd, TodoWrite's list).
    /// </summary>
    internal static ToolRegistry BuildRegistryForType(string type, ToolRegistry parent)
    {
        var result = new ToolRegistry();

        if (ToolPolicyByType.TryGetValue(type, out var allowed))
        {
            foreach (var tool in parent.All)
            {
                if (allowed.Contains(tool.Schema.Name))
                    result.Register(tool.CloneForSubagent());
            }
        }
        else
        {
            // general-purpose — every tool the parent has, minus the Agent tool itself
            foreach (var tool in parent.All)
            {
                if (tool.Schema.Name != Zdtllm.Tools.TaskTool.ToolName)
                    result.Register(tool.CloneForSubagent());
            }
        }

        return result;
    }

    internal static string SystemPromptForType(string type) => type switch
    {
        "code-reviewer" => CodeReviewerSystemPrompt,
        "explore" => ExploreSystemPrompt,
        _ => GeneralPurposeSystemPrompt,
    };

    private const string GeneralPurposeSystemPrompt =
        "You are a focused subagent dispatched by a parent agent. You have your own fresh " +
        "context — you do not see the parent's conversation history. Complete the task " +
        "autonomously using the tools available to you and return a concise summary of what " +
        "you did and the result. Be specific and cite file paths / line numbers when relevant.";

    private const string CodeReviewerSystemPrompt =
        "You are a code-review subagent. Your only job is to analyze code rigorously.\n\n" +
        "Rules:\n" +
        "- READ EVERY file mentioned in the task, in full. Do not reason from titles or guesses.\n" +
        "- For each file, walk through line-by-line. Use Grep to confirm patterns across files.\n" +
        "- Look for: SQL injection, XSS (especially user input echoed without escaping in HTML " +
        "context — including <title>, <h1>, attributes, JavaScript, URLs), IDOR, missing CSRF, " +
        "missing authentication or authorization, path traversal, command injection, race " +
        "conditions, type juggling, weak crypto, leaked secrets, insecure deserialization.\n" +
        "- Cite EXACT file:line for every finding. Quote the offending code.\n" +
        "- You CANNOT modify files (you have only Read, Glob, Grep, TodoWrite).\n" +
        "- Trace EVERY untrusted input source ($_GET, $_POST, $_COOKIE, $_REQUEST, headers, " +
        "session) through to every output sink. Do not declare a vulnerability \"mitigated\" " +
        "after seeing one good escape — verify every echo / interpolation independently.\n\n" +
        "Return a structured list of findings ordered by severity (Critical → High → Medium → Low), " +
        "then a final one-sentence summary of the worst issue.";

    private const string ExploreSystemPrompt =
        "You are a research subagent. Investigate using Read, Glob, Grep on the local " +
        "filesystem and WebFetch for URLs. Synthesize what you find into a clear, sourced " +
        "answer. Do not modify any files (no write tools available).";
}

/// <summary>
/// Raised when a subagent's RunAsync exhausts its retry+fallback budget. The original
/// exception is preserved as InnerException so callers can diagnose the underlying cause.
/// </summary>
public sealed class SubagentExecutionException : Exception
{
    public SubagentExecutionException(string message, Exception? inner)
        : base(message, inner) { }
}
