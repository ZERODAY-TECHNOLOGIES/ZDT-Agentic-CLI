using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core;

public sealed record AgentLoopOptions
{
    public required string Model { get; init; }
    public int MaxTurns { get; init; } = 30;
    public bool SkipPermissions { get; init; }
    public ToolCallingMode ToolCallingMode { get; init; } = ToolCallingMode.Native;
    public string SystemPrompt { get; init; } =
        "You are zdtllmcli, an autonomous CLI assistant from zer0day.ro. " +
        "Use the provided tools to read files and run shell commands when needed. " +
        "Be concise and prefer concrete answers over speculation.";
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

    public AgentLoop(
        LiteLLMClient client,
        ToolRegistry tools,
        PermissionRuleSet perms,
        AgentLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(perms);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _tools = tools;
        _perms = perms;
        _options = options;
    }

    public async Task<AgentResult> RunOneShotAsync(
        string userPrompt,
        TextWriter output,
        TextWriter status,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userPrompt);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(status);

        var xmlMode = _options.ToolCallingMode == ToolCallingMode.Xml;
        var systemPrompt = xmlMode
            ? BuildXmlSystemPrompt(_options.SystemPrompt, _tools.Schemas)
            : _options.SystemPrompt;

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userPrompt),
        };

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

            await foreach (var chunk in _client.StreamChatAsync(messages, toolDefList, _options.Model, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case ChatChunk.TextDelta td:
                        assistantText.Append(td.Text);
                        // In XML mode we buffer text rather than streaming live — the
                        // assistant emits <function_calls> blocks inline that we don't want
                        // surfacing in the user-facing output until we strip them.
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
                        lastPromptTokens = u.PromptTokens;
                        lastCompletionTokens = u.CompletionTokens;
                        break;

                    case ChatChunk.Done:
                        break;
                }
            }

            var nativeCalls = pending.Values
                .Where(v => v.Id is not null && v.FunctionName is not null)
                .Select(v => new ToolCall(v.Id!, v.FunctionName!, v.Arguments.ToString()))
                .ToImmutableArray();

            // XML calls are only considered when Xml mode is on AND no native calls came back.
            // (If the server somehow emitted both, native wins — they're the canonical signal.)
            IReadOnlyList<ParsedXmlToolCall> xmlCalls = nativeCalls.Length == 0 && xmlMode
                ? XmlToolCallParser.ExtractCalls(assistantText.ToString())
                : [];

            if (nativeCalls.Length == 0 && xmlCalls.Count == 0)
            {
                // No tool calls of any kind → agent has produced its final answer.
                var displayText = xmlMode
                    ? XmlToolCallParser.Strip(assistantText.ToString()).TrimEnd()
                    : assistantText.ToString();

                if (xmlMode && displayText.Length > 0)
                    await output.WriteAsync(displayText.AsMemory(), ct).ConfigureAwait(false);

                if (assistantText.Length > 0)
                    await output.WriteLineAsync().ConfigureAwait(false);

                return new AgentResult(displayText, turn, lastPromptTokens, lastCompletionTokens);
            }

            if (nativeCalls.Length > 0)
            {
                await ExecuteNativeRoundAsync(messages, assistantText, nativeCalls, ctx, output, status, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await ExecuteXmlRoundAsync(messages, assistantText, xmlCalls, turn, ctx, output, status, ct)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Agent exceeded max turns ({_options.MaxTurns}) without completing.");
    }

    private async Task ExecuteNativeRoundAsync(
        List<ChatMessage> messages,
        StringBuilder assistantText,
        ImmutableArray<ToolCall> calls,
        ToolContext ctx,
        TextWriter output,
        TextWriter status,
        CancellationToken ct)
    {
        messages.Add(new ChatMessage(
            Role: "assistant",
            Content: assistantText.Length > 0 ? assistantText.ToString() : null,
            ToolCalls: calls,
            ToolCallId: null));

        if (assistantText.Length > 0)
            await output.WriteLineAsync().ConfigureAwait(false);

        foreach (var call in calls)
        {
            await status.WriteLineAsync(FormatStatusLine(call.FunctionName, call.Arguments))
                .ConfigureAwait(false);

            var resultContent = await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);
            messages.Add(ChatMessage.Tool(call.Id, resultContent));
        }
    }

    private async Task ExecuteXmlRoundAsync(
        List<ChatMessage> messages,
        StringBuilder assistantText,
        IReadOnlyList<ParsedXmlToolCall> xmlCalls,
        int turn,
        ToolContext ctx,
        TextWriter output,
        TextWriter status,
        CancellationToken ct)
    {
        // Show the assistant's prose preamble (with XML stripped) before executing the call.
        var cleaned = XmlToolCallParser.Strip(assistantText.ToString()).Trim();
        if (cleaned.Length > 0)
        {
            await output.WriteAsync(cleaned.AsMemory(), ct).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
        }

        // Keep the original (XML-bearing) text in conversation history so the model's
        // own action is preserved in its context — many models rely on seeing their
        // last <function_calls> block to remember what they invoked.
        messages.Add(ChatMessage.AssistantText(assistantText.ToString()));

        for (var i = 0; i < xmlCalls.Count; i++)
        {
            var xml = xmlCalls[i];
            var syntheticId = $"xml_{turn}_{i}";
            var call = new ToolCall(syntheticId, xml.FunctionName, xml.ArgumentsJson);

            await status.WriteLineAsync(FormatStatusLine(call.FunctionName, call.Arguments))
                .ConfigureAwait(false);

            var resultContent = await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);
            messages.Add(ChatMessage.User(
                $"EXECUTION RESULT of [{call.FunctionName}]:\n{resultContent}"));
        }
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
        $"[{toolName}] {Truncate(arguments, 80)}";

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
