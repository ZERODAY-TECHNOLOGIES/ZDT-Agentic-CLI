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
[JsonDerivedType(typeof(ClearEvent), "clear")]
[JsonDerivedType(typeof(ModelChangedEvent), "modelChanged")]
[JsonDerivedType(typeof(ModeChangedEvent), "modeChanged")]
[JsonDerivedType(typeof(CompactionEvent), "compaction")]
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

/// <summary>
/// Marks a /clear in interactive mode. On Resume(), every message before this
/// event is dropped (the system prompt is kept if KeepSystem is true).
/// </summary>
public sealed record ClearEvent(bool KeepSystem) : SessionEvent;

/// <summary>
/// Records a /model command. On Resume(), updates the session's Model field.
/// </summary>
public sealed record ModelChangedEvent(string Model) : SessionEvent;

/// <summary>
/// Records a /tool-calling command. On Resume(), updates the session's Mode field so
/// the agent loop continues with the same tool-call transport the user last selected.
/// </summary>
public sealed record ModeChangedEvent(ToolCallingMode Mode) : SessionEvent;

/// <summary>
/// Records a /compact (or auto-compact) operation. The KeptMessages list is the
/// authoritative replacement for the in-memory message list — Resume() drops
/// every prior message and rebuilds from this snapshot.
/// </summary>
public sealed record CompactionEvent(IReadOnlyList<MessageSnapshot> KeptMessages) : SessionEvent;

/// <summary>Serializable equivalent of ChatMessage for use inside CompactionEvent.</summary>
public sealed record MessageSnapshot(
    string Role,
    string? Content,
    IReadOnlyList<ToolCallEvent>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record ToolCallEvent(string Id, string Name, string Arguments);
