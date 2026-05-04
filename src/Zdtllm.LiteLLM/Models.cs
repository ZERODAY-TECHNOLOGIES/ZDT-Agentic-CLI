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

/// <summary>
/// One row of a LiteLLM /model/info response. Token-limit fields are nullable
/// because LiteLLM only fills them when the proxy registry has metadata for the
/// underlying model — bare proxies for self-hosted models often leave them null.
/// </summary>
public sealed record ModelInfo(
    string ModelName,
    int? MaxInputTokens,
    int? MaxOutputTokens,
    int? MaxTokens)
{
    /// <summary>
    /// Best guess for the usable input context size: prefer max_input_tokens,
    /// fall back to max_tokens (which on most LiteLLM entries is the total
    /// budget). Returns null if neither is provided.
    /// </summary>
    public int? EffectiveContextWindow => MaxInputTokens ?? MaxTokens;
}

public abstract record ChatChunk
{
    private ChatChunk() { }

    public sealed record TextDelta(string Text) : ChatChunk;

    /// <summary>
    /// Chain-of-thought delta from reasoning models (DeepSeek V3.x, R1-style, etc.) that
    /// emit <c>delta.reasoning_content</c> alongside or before <c>delta.content</c>. Per
    /// the OpenAI/DeepSeek spec this stream is ephemeral — consumers must NOT include it
    /// in tool extraction, observer events, or session history sent back to the model.
    /// </summary>
    public sealed record ReasoningDelta(string Text) : ChatChunk;

    public sealed record ToolCallDelta(
        int Index,
        string? Id,
        string? FunctionName,
        string? ArgumentsDelta) : ChatChunk;

    public sealed record Usage(int PromptTokens, int CompletionTokens) : ChatChunk;

    public sealed record Done(string? FinishReason) : ChatChunk;
}
