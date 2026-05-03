using System.Collections.Immutable;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Sessions;

/// <summary>
/// A live conversation. Holds the in-memory ChatMessage history plus optional
/// SessionStore that persists every mutation to disk as a JSONL event stream.
/// AgentLoop uses this as the unit of state across turns.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly List<ChatMessage> _messages = new();
    private readonly SessionStore? _store;

    public string Id { get; }
    public string Model { get; private set; }
    public string? Name { get; private set; }
    public ToolCallingMode Mode { get; private set; }
    public IReadOnlyList<ChatMessage> Messages => _messages;
    public bool IsPersistent => _store is not null;

    private Session(string id, string model, string? name, ToolCallingMode mode, SessionStore? store)
    {
        Id = id;
        Model = model;
        Name = name;
        Mode = mode;
        _store = store;
    }

    /// <summary>Brand-new in-memory session. No persistence.</summary>
    public static Session NewEphemeral(string model, ToolCallingMode mode = ToolCallingMode.Native)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        return new Session(Guid.NewGuid().ToString(), model, name: null, mode, store: null);
    }

    /// <summary>Brand-new persistent session backed by the given store.</summary>
    public static Session NewPersistent(SessionStore store, string model, string? name = null, ToolCallingMode mode = ToolCallingMode.Native)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(model);
        var session = new Session(store.SessionId, model, name, mode, store);
        store.Append(new MetaEvent(store.SessionId, model, name, mode));
        return session;
    }

    /// <summary>Resume from an existing JSONL file. Replays events into in-memory state.</summary>
    public static Session Resume(SessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        string? model = null;
        string? name = null;
        var mode = ToolCallingMode.Native;
        var msgs = new List<ChatMessage>();

        foreach (var ev in store.ReadAll())
        {
            switch (ev)
            {
                case MetaEvent meta:
                    model = meta.Model;
                    name = meta.Name;
                    mode = meta.Mode;
                    break;
                case SystemEvent s:
                    msgs.Add(ChatMessage.System(s.Content));
                    break;
                case UserEvent u:
                    msgs.Add(ChatMessage.User(u.Content));
                    break;
                case AssistantEvent a:
                    msgs.Add(ToAssistantMessage(a));
                    break;
                case ToolEvent t:
                    msgs.Add(ChatMessage.Tool(t.ToolCallId, t.Content));
                    break;
                case ClearEvent clear:
                    var systemMessage = clear.KeepSystem
                        ? msgs.FirstOrDefault(m => m.Role == "system")
                        : null;
                    msgs.Clear();
                    if (systemMessage is not null) msgs.Add(systemMessage);
                    break;
                case ModelChangedEvent mc:
                    model = mc.Model;
                    break;
                case CompactionEvent compact:
                    msgs.Clear();
                    foreach (var snapshot in compact.KeptMessages)
                        msgs.Add(SnapshotToChatMessage(snapshot));
                    break;
                case UsageEvent:
                    // not a chat message — ignore for replay
                    break;
            }
        }

        if (model is null)
            throw new InvalidOperationException(
                $"Session file '{store.Path}' has no meta event; cannot resume.");

        var session = new Session(store.SessionId, model, name, mode, store);
        session._messages.AddRange(msgs);
        return session;
    }

    public void AddSystem(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        _messages.Add(ChatMessage.System(content));
        _store?.Append(new SystemEvent(content));
    }

    public void AddUser(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        _messages.Add(ChatMessage.User(content));
        _store?.Append(new UserEvent(content));
    }

    public void AddAssistant(string? content, ImmutableArray<ToolCall> toolCalls = default)
    {
        var calls = toolCalls.IsDefault ? ImmutableArray<ToolCall>.Empty : toolCalls;
        _messages.Add(new ChatMessage(
            Role: "assistant",
            Content: content,
            ToolCalls: calls,
            ToolCallId: null));
        _store?.Append(new AssistantEvent(
            Content: content,
            ToolCalls: calls.IsEmpty
                ? null
                : calls.Select(tc => new ToolCallEvent(tc.Id, tc.FunctionName, tc.Arguments)).ToArray()));
    }

    public void AddTool(string toolCallId, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        _messages.Add(ChatMessage.Tool(toolCallId, content));
        _store?.Append(new ToolEvent(toolCallId, content));
    }

    public void AddUsage(int promptTokens, int completionTokens)
    {
        _store?.Append(new UsageEvent(promptTokens, completionTokens));
    }

    public void Rename(string? newName)
    {
        Name = newName;
        // Note: not appending a new MetaEvent here — Resume() takes the FIRST meta event
        // it sees as authoritative. Renames during a session are a Phase 3 concern.
    }

    public void ClearKeepingSystem()
    {
        var systemMessage = _messages.FirstOrDefault(m => m.Role == "system");
        _messages.Clear();
        if (systemMessage is not null) _messages.Add(systemMessage);
        _store?.Append(new ClearEvent(KeepSystem: systemMessage is not null));
    }

    public void SetModel(string newModel)
    {
        ArgumentException.ThrowIfNullOrEmpty(newModel);
        Model = newModel;
        _store?.Append(new ModelChangedEvent(newModel));
    }

    /// <summary>
    /// Replace the in-memory message list with the supplied snapshot and persist a
    /// CompactionEvent so Resume() rebuilds from this point. The previous events stay
    /// in the JSONL file for forensics, but Resume() ignores them once a CompactionEvent
    /// is encountered.
    /// </summary>
    public void Compact(IReadOnlyList<ChatMessage> keptMessages)
    {
        ArgumentNullException.ThrowIfNull(keptMessages);

        _messages.Clear();
        _messages.AddRange(keptMessages);

        var snapshots = keptMessages.Select(ChatMessageToSnapshot).ToList();
        _store?.Append(new CompactionEvent(snapshots));
    }

    private static MessageSnapshot ChatMessageToSnapshot(ChatMessage m) => new(
        Role: m.Role,
        Content: m.Content,
        ToolCalls: m.ToolCalls.IsDefaultOrEmpty
            ? null
            : m.ToolCalls.Select(tc => new ToolCallEvent(tc.Id, tc.FunctionName, tc.Arguments)).ToArray(),
        ToolCallId: m.ToolCallId);

    private static ChatMessage SnapshotToChatMessage(MessageSnapshot s)
    {
        var calls = s.ToolCalls is null || s.ToolCalls.Count == 0
            ? ImmutableArray<ToolCall>.Empty
            : s.ToolCalls.Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments)).ToImmutableArray();
        return new ChatMessage(s.Role, s.Content, calls, s.ToolCallId);
    }

    public void Dispose() => _store?.Dispose();

    private static ChatMessage ToAssistantMessage(AssistantEvent ev)
    {
        var calls = ev.ToolCalls is null || ev.ToolCalls.Count == 0
            ? ImmutableArray<ToolCall>.Empty
            : ev.ToolCalls.Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments)).ToImmutableArray();
        return new ChatMessage(
            Role: "assistant",
            Content: ev.Content,
            ToolCalls: calls,
            ToolCallId: null);
    }
}
