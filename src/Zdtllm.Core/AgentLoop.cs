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

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(_options.SystemPrompt),
            ChatMessage.User(userPrompt),
        };

        var toolDefs = _tools.Schemas
            .Select(s => new ToolDef(s.Name, s.Description, s.Parameters))
            .ToList();
        IReadOnlyList<ToolDef>? toolDefList = toolDefs.Count > 0 ? toolDefs : null;

        var ctx = new ToolContext(Cwd: Directory.GetCurrentDirectory());

        int? lastPromptTokens = null;
        int? lastCompletionTokens = null;
        var finalText = new StringBuilder();

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
                        await output.WriteAsync(td.Text.AsMemory(), ct).ConfigureAwait(false);
                        await output.FlushAsync(ct).ConfigureAwait(false);
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

            if (pending.Count == 0)
            {
                if (assistantText.Length > 0)
                    await output.WriteLineAsync().ConfigureAwait(false);
                finalText.Clear().Append(assistantText);
                return new AgentResult(finalText.ToString(), turn, lastPromptTokens, lastCompletionTokens);
            }

            var calls = pending.Values
                .Where(v => v.Id is not null && v.FunctionName is not null)
                .Select(v => new ToolCall(v.Id!, v.FunctionName!, v.Arguments.ToString()))
                .ToImmutableArray();

            messages.Add(new ChatMessage(
                Role: "assistant",
                Content: assistantText.Length > 0 ? assistantText.ToString() : null,
                ToolCalls: calls,
                ToolCallId: null));

            if (assistantText.Length > 0)
                await output.WriteLineAsync().ConfigureAwait(false);

            foreach (var call in calls)
            {
                await status.WriteLineAsync($"[{call.FunctionName}] {Truncate(call.Arguments, 80)}").ConfigureAwait(false);

                var resultContent = await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);
                messages.Add(ChatMessage.Tool(call.Id, resultContent));
            }
        }

        throw new InvalidOperationException(
            $"Agent exceeded max turns ({_options.MaxTurns}) without completing.");
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? FunctionName { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
