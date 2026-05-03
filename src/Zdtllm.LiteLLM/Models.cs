using System.Collections.Immutable;
using System.Text.Json;

namespace Zdtllm.LiteLLM;

public sealed record ChatMessage(
    string Role,
    string? Content,
    ImmutableArray<ToolCall> ToolCalls,
    string? ToolCallId)
{
    public static ChatMessage System(string content) =>
        new("system", content, ImmutableArray<ToolCall>.Empty, ToolCallId: null);

    public static ChatMessage User(string content) =>
        new("user", content, ImmutableArray<ToolCall>.Empty, ToolCallId: null);

    public static ChatMessage AssistantText(string content) =>
        new("assistant", content, ImmutableArray<ToolCall>.Empty, ToolCallId: null);

    public static ChatMessage AssistantToolCalls(IEnumerable<ToolCall> toolCalls) =>
        new("assistant", Content: null, [..toolCalls], ToolCallId: null);

    public static ChatMessage Tool(string toolCallId, string content) =>
        new("tool", content, ImmutableArray<ToolCall>.Empty, toolCallId);
}

public sealed record ToolCall(string Id, string FunctionName, string Arguments);

public sealed record ToolDef(string Name, string Description, JsonElement Parameters);

public abstract record ChatChunk
{
    private ChatChunk() { }

    public sealed record TextDelta(string Text) : ChatChunk;

    public sealed record ToolCallDelta(
        int Index,
        string? Id,
        string? FunctionName,
        string? ArgumentsDelta) : ChatChunk;

    public sealed record Usage(int PromptTokens, int CompletionTokens) : ChatChunk;

    public sealed record Done(string? FinishReason) : ChatChunk;
}
