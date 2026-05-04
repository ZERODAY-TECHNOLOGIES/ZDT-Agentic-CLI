using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Zdtllm.Core.Sessions;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core;

public sealed record AgentLoopOptions
{
    public const string DefaultSystemPrompt =
        "You are zdtllmcli, an autonomous CLI assistant from zer0day.ro. " +
        "Use the provided tools to read files and run shell commands when needed. " +
        "Be concise and prefer concrete answers over speculation.";

    public required string Model { get; init; }
    public int MaxTurns { get; init; } = 30;
    public bool SkipPermissions { get; init; }
    public ToolCallingMode ToolCallingMode { get; init; } = ToolCallingMode.Native;
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    /// <summary>
    /// Cap on concurrent tool executions when a batch is parallel-eligible. 0 or
    /// negative means "no cap" (the historical Task.WhenAll behaviour). The cap
    /// matters mainly for Task-tool fan-out: each parallel subagent triggers its
    /// own LiteLLM stream, and most proxies enforce per-key rate limits.
    /// </summary>
    public int MaxParallel { get; init; } = 0;
}

public sealed record AgentResult(
    string FinalText,
    int Turns,
    int? PromptTokens,
    int? CompletionTokens);

public sealed class AgentLoop
{
    private static readonly Color BrandCyan = new(0x1B, 0xEA, 0xCD);
    private static readonly Color MuteText = new(0x68, 0x7B, 0x89);

    private readonly LiteLLMClient _client;
    private readonly ToolRegistry _tools;
    private readonly PermissionRuleSet _perms;
    private readonly AgentLoopOptions _options;
    private readonly ContextManager? _context;
    private readonly IAnsiConsole? _richConsole;
    private readonly IAgentObserver? _observer;

    public AgentLoop(
        LiteLLMClient client,
        ToolRegistry tools,
        PermissionRuleSet perms,
        AgentLoopOptions options,
        ContextManager? context = null,
        IAnsiConsole? richConsole = null,
        IAgentObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(perms);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _tools = tools;
        _perms = perms;
        _options = options;
        _context = context;
        _richConsole = richConsole;
        _observer = observer;
    }

    public PermissionRuleSet Permissions => _perms;
    public ToolRegistry Tools => _tools;
    public LiteLLMClient Client => _client;
    public ContextManager? Context => _context;
    public AgentLoopOptions Options => _options;
    public IAnsiConsole? RichConsole => _richConsole;
    public IAgentObserver? Observer => _observer;

    /// <summary>
    /// Backwards-compatible one-shot entry point: spins up an ephemeral
    /// (non-persistent) session, runs a single user→assistant exchange, and
    /// returns the final answer. Equivalent to creating Session.NewEphemeral
    /// and calling RunTurnAsync.
    /// </summary>
    public Task<AgentResult> RunOneShotAsync(
        string userPrompt,
        TextWriter output,
        TextWriter status,
        CancellationToken ct = default)
    {
        var session = Session.NewEphemeral(_options.Model, _options.ToolCallingMode);
        return RunTurnAsync(session, userPrompt, output, status, ct);
    }

    /// <summary>
    /// Runs a single user turn against the given session. Mutates the session
    /// (adds the user message, the assistant response, and any tool calls /
    /// results that occurred). On a fresh session this also bootstraps the
    /// system prompt — subsequent turns reuse the existing one. The session's
    /// Model and Mode are authoritative; AgentLoopOptions are only consulted
    /// for ephemeral session bootstrap and for system-prompt content.
    /// </summary>
    public async Task<AgentResult> RunTurnAsync(
        Session session,
        string userPrompt,
        TextWriter output,
        TextWriter status,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(userPrompt);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(status);

        var xmlMode = session.Mode == ToolCallingMode.Xml;

        // Bootstrap system prompt the first time the session is touched.
        if (session.Messages.Count == 0)
        {
            var systemPrompt = xmlMode
                ? BuildXmlSystemPrompt(_options.SystemPrompt, _tools.Schemas)
                : _options.SystemPrompt;
            session.AddSystem(systemPrompt);
        }

        session.AddUser(userPrompt);

        IReadOnlyList<ToolDef>? toolDefList = null;
        if (!xmlMode)
        {
            var defs = _tools.Schemas
                .Select(s => new ToolDef(s.Name, s.Description, s.Parameters))
                .ToList();
            toolDefList = defs.Count > 0 ? defs : null;
        }

        var ctx = new ToolContext(Cwd: Directory.GetCurrentDirectory());
        int? lastPromptTokens = null;
        int? lastCompletionTokens = null;

        for (var turn = 1; turn <= _options.MaxTurns; turn++)
        {
            var assistantText = new StringBuilder();
            var pending = new SortedDictionary<int, ToolCallAccumulator>();
            int? turnPromptTokens = null;
            int? turnCompletionTokens = null;

            async Task ConsumeStreamAsync()
            {
                await foreach (var chunk in _client.StreamChatAsync(session.Messages, toolDefList, session.Model, ct).ConfigureAwait(false))
                {
                    switch (chunk)
                    {
                        case ChatChunk.TextDelta td:
                            assistantText.Append(td.Text);
                            // Observer always sees raw deltas — that's what stream-json wants. The
                            // text writer / rich console split below is purely for the human-facing
                            // terminal, which the observer pipeline doesn't go through.
                            await SafeNotifyAsync(_observer?.OnTextDeltaAsync(td.Text, ct)).ConfigureAwait(false);
                            // Rich console suppresses per-delta writes — markdown gets rendered as one block
                            // once the stream completes (or just before tool dispatch). Keeps terminal clean
                            // while the thinking spinner runs.
                            if (!xmlMode && _richConsole is null)
                            {
                                await output.WriteAsync(td.Text.AsMemory(), ct).ConfigureAwait(false);
                                await output.FlushAsync(ct).ConfigureAwait(false);
                            }
                            break;

                        case ChatChunk.ToolCallDelta tcd:
                            if (!pending.TryGetValue(tcd.Index, out var acc))
                                pending[tcd.Index] = acc = new ToolCallAccumulator();
                            if (tcd.Id is not null) acc.Id = tcd.Id;
                            if (tcd.FunctionName is not null) acc.FunctionName = tcd.FunctionName;
                            if (tcd.ArgumentsDelta is not null) acc.Arguments.Append(tcd.ArgumentsDelta);
                            break;

                        case ChatChunk.Usage u:
                            turnPromptTokens = u.PromptTokens;
                            turnCompletionTokens = u.CompletionTokens;
                            lastPromptTokens = u.PromptTokens;
                            lastCompletionTokens = u.CompletionTokens;
                            break;

                        case ChatChunk.Done:
                            break;
                    }
                }
            }

            if (_richConsole is not null)
            {
                await _richConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(new Style(BrandCyan))
                    .StartAsync($"[{Hex(BrandCyan)}]thinking[/]", async _ => await ConsumeStreamAsync())
                    .ConfigureAwait(false);
            }
            else
            {
                await ConsumeStreamAsync().ConfigureAwait(false);
            }

            if (turnPromptTokens is int p && turnCompletionTokens is int c)
            {
                session.AddUsage(p, c);
                _context?.RegisterTurn(p, c);
            }

            var nativeCalls = pending.Values
                .Where(v => v.Id is not null && v.FunctionName is not null)
                .Select(v => new ToolCall(v.Id!, v.FunctionName!, v.Arguments.ToString()))
                .ToImmutableArray();

            IReadOnlyList<ParsedXmlToolCall> xmlCalls = nativeCalls.Length == 0 && xmlMode
                ? XmlToolCallParser.ExtractCalls(assistantText.ToString())
                : [];

            if (nativeCalls.Length == 0 && xmlCalls.Count == 0)
            {
                var displayText = xmlMode
                    ? XmlToolCallParser.Strip(assistantText.ToString()).TrimEnd()
                    : assistantText.ToString();

                if (_richConsole is not null && displayText.Length > 0)
                {
                    // Rich path: text was buffered (not streamed). Render as markdown now.
                    _richConsole.Write(MarkdownRenderer.Render(displayText));
                    _richConsole.WriteLine();
                }
                else
                {
                    if (xmlMode && displayText.Length > 0)
                        await output.WriteAsync(displayText.AsMemory(), ct).ConfigureAwait(false);

                    if (assistantText.Length > 0)
                        await output.WriteLineAsync().ConfigureAwait(false);
                }

                session.AddAssistant(
                    content: displayText.Length > 0 ? displayText : null,
                    toolCalls: ImmutableArray<ToolCall>.Empty);

                await SafeNotifyAsync(_observer?.OnFinalAsync(
                    displayText, turn, lastPromptTokens, lastCompletionTokens, ct)).ConfigureAwait(false);
                return new AgentResult(displayText, turn, lastPromptTokens, lastCompletionTokens);
            }

            if (nativeCalls.Length > 0)
            {
                await ExecuteNativeRoundAsync(session, assistantText, nativeCalls, ctx, output, status, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await ExecuteXmlRoundAsync(session, assistantText, xmlCalls, turn, ctx, output, status, ct)
                    .ConfigureAwait(false);
            }

            // Mid-turn auto-compact: if the just-finished iteration pushed us past
            // the hard threshold, summarise older history before the next iteration
            // sends an even bigger context. This is the only path that fires inside
            // a subagent (subagents have their own ContextManager and never hit the
            // pre-prompt path that the parent's REPL might run).
            if (_context is not null && _context.IsBeyondHardThreshold)
            {
                await status.WriteLineAsync(
                    Palette.Red($"[auto-compact at {_context.UsagePercent}%]") + " " +
                    Palette.Mute("summarising older turns mid-task to free context"))
                    .ConfigureAwait(false);
                try
                {
                    await _context.CompactAsync(session, _client, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await status.WriteLineAsync(Palette.Red($"auto-compact failed: {ex.Message}"))
                        .ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            $"Agent exceeded max turns ({_options.MaxTurns}) without completing.");
    }

    private async Task ExecuteNativeRoundAsync(
        Session session,
        StringBuilder assistantText,
        ImmutableArray<ToolCall> calls,
        ToolContext ctx,
        TextWriter output,
        TextWriter status,
        CancellationToken ct)
    {
        session.AddAssistant(
            content: assistantText.Length > 0 ? assistantText.ToString() : null,
            toolCalls: calls);

        if (assistantText.Length > 0)
        {
            if (_richConsole is not null)
            {
                // Render the model's pre-toolcall narration as markdown before the tool spinner kicks in.
                _richConsole.Write(MarkdownRenderer.Render(assistantText.ToString()));
                _richConsole.WriteLine();
            }
            else
            {
                await output.WriteLineAsync().ConfigureAwait(false);
            }
        }

        var results = await DispatchToolCallsAsync(calls, ctx, status, ct).ConfigureAwait(false);
        for (var i = 0; i < calls.Length; i++)
            session.AddTool(calls[i].Id, results[i]);
    }

    private async Task ExecuteXmlRoundAsync(
        Session session,
        StringBuilder assistantText,
        IReadOnlyList<ParsedXmlToolCall> xmlCalls,
        int turn,
        ToolContext ctx,
        TextWriter output,
        TextWriter status,
        CancellationToken ct)
    {
        var cleaned = XmlToolCallParser.Strip(assistantText.ToString()).Trim();
        if (cleaned.Length > 0)
        {
            if (_richConsole is not null)
            {
                _richConsole.Write(MarkdownRenderer.Render(cleaned));
                _richConsole.WriteLine();
            }
            else
            {
                await output.WriteAsync(cleaned.AsMemory(), ct).ConfigureAwait(false);
                await output.WriteLineAsync().ConfigureAwait(false);
            }
        }

        // Persist the original XML-bearing text so the model's own action survives
        // session resumes and feeds back into its context next turn.
        session.AddAssistant(content: assistantText.ToString(), toolCalls: ImmutableArray<ToolCall>.Empty);

        var calls = ImmutableArray.CreateBuilder<ToolCall>(xmlCalls.Count);
        for (var i = 0; i < xmlCalls.Count; i++)
        {
            var xml = xmlCalls[i];
            calls.Add(new ToolCall($"xml_{turn}_{i}", xml.FunctionName, xml.ArgumentsJson));
        }
        var callsArr = calls.ToImmutable();

        var results = await DispatchToolCallsAsync(callsArr, ctx, status, ct).ConfigureAwait(false);
        for (var i = 0; i < callsArr.Length; i++)
        {
            session.AddUser($"EXECUTION RESULT of [{callsArr[i].FunctionName}]:\n{results[i]}");
        }
    }

    /// <summary>
    /// Run a batch of tool calls, parallelising via Task.WhenAll when every tool
    /// in the batch reports CanRunInParallel, otherwise serialising. Status lines
    /// are emitted before dispatch so the user sees both calls register up-front
    /// when running concurrently. Each call's result text is returned in the same
    /// order the calls were given so the caller can pair them with tool_call_ids.
    /// </summary>
    private async Task<string[]> DispatchToolCallsAsync(
        ImmutableArray<ToolCall> calls,
        ToolContext ctx,
        TextWriter status,
        CancellationToken ct)
    {
        if (calls.Length == 0) return Array.Empty<string>();

        var allParallelisable = calls.All(c =>
        {
            var tool = _tools.Get(c.FunctionName);
            return tool is not null && tool.CanRunInParallel;
        });

        // For parallel batches we always print the per-call status lines up-front (so the user
        // sees what is firing concurrently). For sequential single-tool dispatch with a rich
        // console, we suppress the static line and use a Status spinner instead. When an
        // observer is wired in (--verbose, --output-format) it owns the user-facing trace, so
        // we suppress the legacy status print to avoid duplicate lines.
        var useSpinnerPerCall = _richConsole is not null
            && (!allParallelisable || calls.Length == 1);

        if (!useSpinnerPerCall && _observer is null)
        {
            foreach (var call in calls)
            {
                await status.WriteLineAsync(FormatStatusLine(call.FunctionName, call.Arguments))
                    .ConfigureAwait(false);
            }
            if (allParallelisable && calls.Length > 1)
            {
                await status.WriteLineAsync(Palette.Mute($"  ↳ dispatching {calls.Length} calls in parallel"))
                    .ConfigureAwait(false);
            }
        }

        if (!allParallelisable || calls.Length == 1)
        {
            var sequential = new string[calls.Length];
            for (var i = 0; i < calls.Length; i++)
                sequential[i] = await ExecuteToolWithSpinnerAsync(calls[i], ctx, useSpinnerPerCall, ct).ConfigureAwait(false);
            return sequential;
        }

        var maxParallel = _options.MaxParallel;
        if (maxParallel > 0 && maxParallel < calls.Length)
        {
            // Throttled fan-out — semaphore caps concurrent in-flight executions. Useful when
            // the parent dispatches a fan of Task subagents and the LiteLLM proxy is rate-limited.
            await status.WriteLineAsync(
                Palette.Mute($"  ↳ throttled to {maxParallel} concurrent (--max-parallel)"))
                .ConfigureAwait(false);

            using var sem = new SemaphoreSlim(maxParallel);
            var throttled = new Task<string>[calls.Length];
            for (var i = 0; i < calls.Length; i++)
            {
                var call = calls[i];
                throttled[i] = RunWithSemaphore(sem, call, ctx, ct);
            }
            return await Task.WhenAll(throttled).ConfigureAwait(false);
        }

        var tasks = new Task<string>[calls.Length];
        for (var i = 0; i < calls.Length; i++)
            tasks[i] = ExecuteToolAsync(calls[i], ctx, ct);
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<string> RunWithSemaphore(
        SemaphoreSlim sem, ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { return await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false); }
        finally { sem.Release(); }
    }

    private async Task<string> ExecuteToolWithSpinnerAsync(
        ToolCall call,
        ToolContext ctx,
        bool useSpinner,
        CancellationToken ct)
    {
        if (!useSpinner || _richConsole is null)
            return await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);

        var label = $"[{Hex(BrandCyan)}]{Markup.Escape(call.FunctionName)}[/] " +
                    $"[{Hex(MuteText)}]{Markup.Escape(Truncate(call.Arguments, 80))}[/]";
        string result = string.Empty;
        await _richConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(BrandCyan))
            .StartAsync(label, async _ =>
            {
                result = await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        // Print a confirmation line after the spinner clears so the call stays in scrollback.
        _richConsole.MarkupLine($"[{Hex(BrandCyan)}]✓[/] {label}");
        return result;
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        await SafeNotifyAsync(_observer?.OnToolCallAsync(call.FunctionName, call.Arguments, ct)).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (content, isError) = await ExecuteToolCoreAsync(call, ctx, ct).ConfigureAwait(false);
        sw.Stop();
        await SafeNotifyAsync(_observer?.OnToolResultAsync(call.FunctionName, content, isError, sw.Elapsed, ct))
            .ConfigureAwait(false);
        return content;
    }

    private async Task<(string Content, bool IsError)> ExecuteToolCoreAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var tool = _tools.Get(call.FunctionName);
        if (tool is null)
            return ($"[Unknown tool: {call.FunctionName}]", true);

        JsonDocument argsDoc;
        try
        {
            argsDoc = string.IsNullOrWhiteSpace(call.Arguments)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(call.Arguments);
        }
        catch (JsonException ex)
        {
            return ($"[Invalid arguments JSON for {call.FunctionName}: {ex.Message}]", true);
        }

        using (argsDoc)
        {
            var args = argsDoc.RootElement;
            var specifier = tool.GetSpecifierForPermissions(args);
            var decision = _perms.Evaluate(call.FunctionName, specifier);

            if (decision == PermissionDecision.Deny)
                return ($"[Permission denied: {call.FunctionName}({specifier ?? string.Empty})]", true);

            if (decision == PermissionDecision.Ask && !_options.SkipPermissions)
                return ($"[Permission required for {call.FunctionName}({specifier ?? string.Empty}). " +
                       "Add an allow rule in settings.json or pass --dangerously-skip-permissions.]", true);

            var res = await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false);
            return (res.Content, res.IsError);
        }
    }

    /// <summary>
    /// Best-effort observer notification: awaits the supplied task but swallows any exception
    /// (and respects null) so a misbehaving observer can't crash the agent. Cancellations
    /// propagate normally — they're a legitimate caller signal, not an observer bug.
    /// </summary>
    private static async Task SafeNotifyAsync(Task? notification)
    {
        if (notification is null) return;
        try { await notification.ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* swallow — observers are decorative */ }
    }

    private static string FormatStatusLine(string toolName, string arguments) =>
        $"{Palette.Cyan($"[{toolName}]")} {Palette.Mute(Truncate(arguments, 80))}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static string BuildXmlSystemPrompt(string baseSystem, ImmutableArray<ToolSchema> schemas)
    {
        var sb = new StringBuilder();
        sb.AppendLine(baseSystem);
        sb.AppendLine();
        sb.AppendLine("# Tool calling protocol");
        sb.AppendLine();
        sb.AppendLine("You have access to the tools listed below. To invoke a tool, write a single");
        sb.AppendLine("<function_calls> block at the END of your reply, then stop and wait:");
        sb.AppendLine();
        sb.AppendLine("<function_calls>");
        sb.AppendLine("<invoke name=\"ToolName\">");
        sb.AppendLine("<parameter name=\"argName\">value</parameter>");
        sb.AppendLine("</invoke>");
        sb.AppendLine("</function_calls>");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use real tool names from the catalog below — they are case-sensitive.");
        sb.AppendLine("- Each <parameter> must use double-quoted name attributes.");
        sb.AppendLine("- Provide tool arguments as raw text inside the parameter element. JSON objects/arrays are also accepted as the value.");
        sb.AppendLine("- Do NOT fabricate the tool's output. After your <function_calls> block, stop. The user will reply with an `EXECUTION RESULT of [Tool]:` message containing the actual output.");
        sb.AppendLine("- Only after seeing the EXECUTION RESULT may you continue or invoke another tool.");
        sb.AppendLine("- When you have the final answer for the user, respond as plain prose without any <function_calls> block.");
        sb.AppendLine();

        if (schemas.IsDefaultOrEmpty)
        {
            sb.AppendLine("(No tools are currently available.)");
            return sb.ToString();
        }

        sb.AppendLine("# Tool catalog");
        sb.AppendLine();
        foreach (var schema in schemas)
        {
            sb.AppendLine($"## {schema.Name}");
            sb.AppendLine(schema.Description);
            sb.AppendLine();
            AppendParameters(sb, schema.Parameters);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendParameters(StringBuilder sb, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return;

        if (!parameters.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("(no parameters)");
            return;
        }

        var requiredSet = new HashSet<string>(StringComparer.Ordinal);
        if (parameters.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    requiredSet.Add(item.GetString()!);
            }
        }

        sb.AppendLine("Parameters:");
        foreach (var prop in props.EnumerateObject())
        {
            var name = prop.Name;
            var desc = prop.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : string.Empty;
            var type = prop.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;
            var requiredMarker = requiredSet.Contains(name) ? " (required)" : string.Empty;
            sb.AppendLine($"- `{name}` ({type}){requiredMarker}: {desc}");
        }
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? FunctionName { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
