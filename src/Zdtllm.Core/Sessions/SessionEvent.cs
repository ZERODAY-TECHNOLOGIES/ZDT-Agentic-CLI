using System.Text.Json.Serialization;

namespace Zdtllm.Core.Sessions;

/// <summary>
/// One line of a session JSONL file. Polymorphic on the "type" discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MetaEvent), "meta")]
[JsonDerivedType(typeof(SystemEvent), "system")]
[JsonDerivedType(typeof(UserEvent), "user")]
[JsonDerivedType(typeof(AssistantEvent), "assistant")]
[JsonDerivedType(typeof(ToolEvent), "tool")]
[JsonDerivedType(typeof(UsageEvent), "usage")]
public abstract record SessionEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MetaEvent(
    string SessionId,
    string Model,
    string? Name = null,
    ToolCallingMode Mode = ToolCallingMode.Native) : SessionEvent;

public sealed record SystemEvent(string Content) : SessionEvent;

public sealed record UserEvent(string Content) : SessionEvent;

public sealed record AssistantEvent(
    string? Content,
    IReadOnlyList<ToolCallEvent>? ToolCalls = null) : SessionEvent;

public sealed record ToolEvent(string ToolCallId, string Content) : SessionEvent;

public sealed record UsageEvent(int PromptTokens, int CompletionTokens) : SessionEvent;

public sealed record ToolCallEvent(string Id, string Name, string Arguments);
