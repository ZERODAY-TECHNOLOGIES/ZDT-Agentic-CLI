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
                case ModeChangedEvent md:
                    mode = md.Mode;
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

        // Heal sessions compacted by an older build. Chat templates for Qwen3.x / GLM reject any
        // system message that is not the first ("System message must be at the beginning"); a stale
        // compaction snapshot stored the summary as a SECOND system message, so merge every system
        // message into the first before the replayed history is ever sent.
        CoalesceSystemMessages(msgs);

        var session = new Session(store.SessionId, model, name, mode, store);
        session._messages.AddRange(msgs);
        return session;
    }

    /// <summary>
    /// Collapse multiple system messages into a single leading one (contents joined, order kept).
    /// A no-op when there are 0 or 1 system messages. Needed because some chat templates raise
    /// "System message must be at the beginning" for any non-leading system message.
    /// </summary>
    private static void CoalesceSystemMessages(List<ChatMessage> msgs)
    {
        var idx = new List<int>();
        for (var i = 0; i < msgs.Count; i++)
            if (msgs[i].Role == "system") idx.Add(i);
        if (idx.Count <= 1) return;

        var merged = string.Join("\n\n",
            idx.Select(i => msgs[i].Content).Where(c => !string.IsNullOrEmpty(c)));
        // Remove the trailing system messages first so earlier indices stay valid, then fold all
        // content into the first system slot (its index is unaffected by the later removals).
        for (var k = idx.Count - 1; k >= 1; k--) msgs.RemoveAt(idx[k]);
        msgs[idx[0]] = msgs[idx[0]] with { Content = merged };
    }

    public void AddSystem(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        _messages.Add(ChatMessage.System(content));
        _store?.Append(new SystemEvent(content));
    }

    public void AddUser(string content, IReadOnlyList<string>? imageDataUrls = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        if (imageDataUrls is { Count: > 0 })
        {
            // Keep the base64 images in the live message so the vision model sees them (and keeps
            // seeing them on later turns — that's how image context works). But do NOT persist the
            // bytes: they'd bloat the JSONL and can't be faithfully replayed on resume anyway.
            // We persist the text plus a note so the transcript still records that images were sent.
            _messages.Add(ChatMessage.UserWithImages(content, imageDataUrls));
            _store?.Append(new UserEvent(
                content + $"\n\n[attached {imageDataUrls.Count} image(s) — not stored in session]"));
        }
        else
        {
            _messages.Add(ChatMessage.User(content));
            _store?.Append(new UserEvent(content));
        }
    }

    /// <summary>
    /// Stand-in content for a degenerate assistant turn — no visible text AND no tool calls. This
    /// happens with a thinking model that emits only <c>reasoning_content</c> (dropped) when the
    /// reasoning-only recovery also finds nothing to surface. An assistant message with neither
    /// content nor tool_calls is INVALID on an OpenAI-compatible endpoint — the server 400s with
    /// "assistant message must contain either 'content' or 'tool_calls'", which then breaks EVERY
    /// turn of a resumed session. Substituting a placeholder keeps the turn well-formed.
    /// </summary>
    private const string EmptyAssistantPlaceholder = "(no response)";

    public void AddAssistant(string? content, ImmutableArray<ToolCall> toolCalls = default)
    {
        var calls = toolCalls.IsDefault ? ImmutableArray<ToolCall>.Empty : toolCalls;
        // Never persist/send a content-less, tool-call-less assistant turn (see EmptyAssistantPlaceholder).
        if (string.IsNullOrEmpty(content) && calls.IsEmpty)
            content = EmptyAssistantPlaceholder;
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

    /// <summary>
    /// One-shot in-memory nudge for the reasoning-only recovery: the model emitted chain-of-thought
    /// but no visible answer, and we want to ask it to produce one WITHOUT creating a second
    /// consecutive user turn — strict-alternation chat templates (Qwen / GLM via vLLM) reject that
    /// with the misleading "System message must be at the beginning". If the last message is already
    /// a user turn, the nudge is folded into it in memory (not persisted — it is a transient retry
    /// aid; resume replays the original turn). Otherwise a normal user turn is appended, which is
    /// valid after an assistant/tool message.
    /// </summary>
    public void NudgeAfterReasoningOnly(string nudge)
    {
        ArgumentException.ThrowIfNullOrEmpty(nudge);
        if (_messages.Count > 0 && _messages[^1].Role == "user")
        {
            var m = _messages[^1];
            _messages[^1] = m with { Content = (m.Content ?? string.Empty) + "\n\n" + nudge };
        }
        else
        {
            AddUser(nudge);
        }
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

    public void SetMode(ToolCallingMode newMode)
    {
        Mode = newMode;
        _store?.Append(new ModeChangedEvent(newMode));
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
        // Heal a degenerate empty assistant turn persisted by an older/interrupted run — replaying it
        // verbatim would 400 the first post-resume request (see EmptyAssistantPlaceholder / AddAssistant).
        var content = string.IsNullOrEmpty(ev.Content) && calls.IsEmpty ? EmptyAssistantPlaceholder : ev.Content;
        return new ChatMessage(
            Role: "assistant",
            Content: content,
            ToolCalls: calls,
            ToolCallId: null);
    }
}
