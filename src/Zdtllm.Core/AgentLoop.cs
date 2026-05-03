using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
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
}

public sealed record AgentResult(
    string FinalText,
    int Turns,
    int? PromptTokens,
    int? CompletionTokens);

public sealed class AgentLoop
{
    private readonly LiteLLMClient _client;
    private readonly ToolRegistry _tools;
    private readonly PermissionRuleSet _perms;
    private readonly AgentLoopOptions _options;
    private readonly ContextManager? _context;

    public AgentLoop(
        LiteLLMClient client,
        ToolRegistry tools,
        PermissionRuleSet perms,
        AgentLoopOptions options,
        ContextManager? context = null)
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
    }

    public PermissionRuleSet Permissions => _perms;
    public ToolRegistry Tools => _tools;
    public LiteLLMClient Client => _client;
    public ContextManager? Context => _context;
    public AgentLoopOptions Options => _options;

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

            await foreach (var chunk in _client.StreamChatAsync(session.Messages, toolDefList, session.Model, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case ChatChunk.TextDelta td:
                        assistantText.Append(td.Text);
                        if (!xmlMode)
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

                if (xmlMode && displayText.Length > 0)
                    await output.WriteAsync(displayText.AsMemory(), ct).ConfigureAwait(false);

                if (assistantText.Length > 0)
                    await output.WriteLineAsync().ConfigureAwait(false);

                session.AddAssistant(
                    content: displayText.Length > 0 ? displayText : null,
                    toolCalls: ImmutableArray<ToolCall>.Empty);

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
            await output.WriteLineAsync().ConfigureAwait(false);

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
            await output.WriteAsync(cleaned.AsMemory(), ct).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
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

        if (!allParallelisable || calls.Length == 1)
        {
            var sequential = new string[calls.Length];
            for (var i = 0; i < calls.Length; i++)
                sequential[i] = await ExecuteToolAsync(calls[i], ctx, ct).ConfigureAwait(false);
            return sequential;
        }

        var tasks = new Task<string>[calls.Length];
        for (var i = 0; i < calls.Length; i++)
            tasks[i] = ExecuteToolAsync(calls[i], ctx, ct);
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var tool = _tools.Get(call.FunctionName);
        if (tool is null)
            return $"[Unknown tool: {call.FunctionName}]";

        JsonDocument argsDoc;
        try
        {
            argsDoc = string.IsNullOrWhiteSpace(call.Arguments)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(call.Arguments);
        }
        catch (JsonException ex)
        {
            return $"[Invalid arguments JSON for {call.FunctionName}: {ex.Message}]";
        }

        using (argsDoc)
        {
            var args = argsDoc.RootElement;
            var specifier = tool.GetSpecifierForPermissions(args);
            var decision = _perms.Evaluate(call.FunctionName, specifier);

            if (decision == PermissionDecision.Deny)
                return $"[Permission denied: {call.FunctionName}({specifier ?? string.Empty})]";

            if (decision == PermissionDecision.Ask && !_options.SkipPermissions)
                return $"[Permission required for {call.FunctionName}({specifier ?? string.Empty}). " +
                       "Add an allow rule in settings.json or pass --dangerously-skip-permissions.]";

            var res = await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false);
            return res.Content;
        }
    }

    private static string FormatStatusLine(string toolName, string arguments) =>
        $"{Palette.Cyan($"[{toolName}]")} {Palette.Mute(Truncate(arguments, 80))}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

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
