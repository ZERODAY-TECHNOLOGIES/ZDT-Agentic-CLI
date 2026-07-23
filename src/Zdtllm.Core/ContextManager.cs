using System.Collections.Immutable;
using Zdtllm.Core.Sessions;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core;

/// <summary>
/// Tracks how full the model's context window is across turns and orchestrates
/// /compact (manual or automatic) when usage gets close to the limit. Owned by Cli;
/// shared by AgentLoop (which feeds in per-turn token counts) and Repl (which acts
/// on threshold flags).
/// </summary>
public sealed class ContextManager
{
    private const string CompactionPrompt =
        "Summarize the following conversation history into a concise recap that preserves: " +
        "decisions made, files modified, errors encountered, and unresolved questions. " +
        "Output plain text, no more than 500 words.";

    private const int KeepLastUserTurns = 4;

    public int ContextWindow { get; }
    public string MediumModel { get; }
    public double SoftThreshold { get; }
    public double HardThreshold { get; }
    public int LastPromptTokens { get; private set; }
    public int LastCompletionTokens { get; private set; }

    public ContextManager(int contextWindow, string mediumModel, double softThreshold = 0.80, double hardThreshold = 0.90)
    {
        if (contextWindow <= 0) throw new ArgumentOutOfRangeException(nameof(contextWindow));
        ArgumentException.ThrowIfNullOrEmpty(mediumModel);
        if (softThreshold <= 0 || softThreshold >= 1) throw new ArgumentOutOfRangeException(nameof(softThreshold));
        if (hardThreshold <= softThreshold || hardThreshold >= 1) throw new ArgumentOutOfRangeException(nameof(hardThreshold));

        ContextWindow = contextWindow;
        MediumModel = mediumModel;
        SoftThreshold = softThreshold;
        HardThreshold = hardThreshold;
    }

    public double UsageFraction => (double)LastPromptTokens / ContextWindow;
    public int UsagePercent => (int)Math.Round(UsageFraction * 100);
    public bool IsBeyondSoftThreshold => UsageFraction >= SoftThreshold;
    public bool IsBeyondHardThreshold => UsageFraction >= HardThreshold;

    /// <summary>
    /// Forward-looking variant of <see cref="IsBeyondHardThreshold"/>. Estimates the size of the
    /// CURRENT session (including any tool results just appended) and reports whether the NEXT
    /// iteration's prompt would blow the hard threshold. Needed because per-turn usage chunks
    /// only count up through the assistant message — tool results land afterwards and the next
    /// iteration sends them all back. Without this check, a turn that read two large files and
    /// crossed 90% would not auto-compact and the next request would hit the LiteLLM 400.
    /// </summary>
    public bool IsProjectedBeyondHardThreshold(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var totalTokens = 0;
        foreach (var (_, t) in EstimateTokensByRole(session)) totalTokens += t;
        return totalTokens >= ContextWindow * HardThreshold;
    }

    /// <summary>
    /// Approximate per-role token usage for the current session, estimated at
    /// 4 chars / token. Used by /context for the breakdown view; the totals
    /// across roles will not match LastPromptTokens exactly because the API's
    /// real tokeniser produces different counts, but the proportions are close
    /// enough to show users which role is eating the budget.
    /// </summary>
    public static IReadOnlyDictionary<string, int> EstimateTokensByRole(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var msg in session.Messages)
        {
            var chars = msg.Content?.Length ?? 0;
            if (!msg.ToolCalls.IsDefaultOrEmpty)
            {
                foreach (var tc in msg.ToolCalls)
                    chars += tc.FunctionName.Length + tc.Arguments.Length + 16;
            }
            if (!string.IsNullOrEmpty(msg.ToolCallId)) chars += msg.ToolCallId.Length;

            var tokens = (chars + 3) / 4;
            result[msg.Role] = (result.TryGetValue(msg.Role, out var existing) ? existing : 0) + tokens;
        }
        return result;
    }

    /// <summary>Called by AgentLoop after each turn's Usage chunk arrives.</summary>
    public void RegisterTurn(int promptTokens, int completionTokens)
    {
        LastPromptTokens = promptTokens;
        LastCompletionTokens = completionTokens;
    }

    /// <summary>
    /// Compact the session: keep the system prompt and the last K user-turn pairs verbatim,
    /// summarise everything in between via the medium model, and replace the middle slice
    /// with one synthetic system message wrapped in &lt;conversation_summary&gt; tags. Returns
    /// the number of messages that were collapsed (0 if nothing was eligible).
    /// </summary>
    public async Task<int> CompactAsync(Session session, LiteLLMClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);

        var msgs = session.Messages;
        var (systemPart, summarizable, tail) = Slice(msgs);

        if (summarizable.Count == 0) return 0;

        var summary = await SummarizeAsync(summarizable, client, ct).ConfigureAwait(false);

        var rebuilt = new List<ChatMessage>(systemPart.Count + 1 + tail.Count);
        rebuilt.AddRange(systemPart);
        rebuilt.Add(ChatMessage.System($"<conversation_summary>\n{summary.Trim()}\n</conversation_summary>"));
        rebuilt.AddRange(tail);

        session.Compact(rebuilt);
        // After compaction the next turn's prompt_tokens count will redo from a much smaller
        // baseline; reset our tracker so we don't keep shouting "context full" until the next
        // server response updates it.
        LastPromptTokens = 0;
        return summarizable.Count;
    }

    /// <summary>
    /// In-turn fallback for when <see cref="CompactAsync"/> cannot help: a single long agentic turn
    /// has exactly ONE user message (tool results live under role "tool"), so <see cref="Slice"/>
    /// yields an empty summarizable body and compaction is a no-op — yet the accumulated tool
    /// results can still blow the context window and 400 the next request. This is the exact
    /// GLM-5.2 pattern: one prompt, dozens of tool rounds, a huge window.
    ///
    /// <para>
    /// We free space by truncating the CONTENT of the oldest tool results in place, keeping the last
    /// <paramref name="keepLastToolResults"/> verbatim (the model almost always needs the most recent
    /// ones). Truncating in place — rather than dropping messages — preserves every assistant⇄tool
    /// pairing and role ordering, so no chat template is broken and no orphan tool message is left
    /// behind. Returns the number of tool results that were shortened (0 if nothing was eligible).
    /// </para>
    /// </summary>
    public int TruncateOldToolResults(Session session, int keepLastToolResults = 3, int perResultCap = 2000)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (keepLastToolResults < 0) throw new ArgumentOutOfRangeException(nameof(keepLastToolResults));
        if (perResultCap <= 0) throw new ArgumentOutOfRangeException(nameof(perResultCap));

        var msgs = session.Messages;

        // Index of every tool-result message, in order.
        var toolIdx = new List<int>();
        for (var i = 0; i < msgs.Count; i++)
            if (msgs[i].Role == "tool") toolIdx.Add(i);

        if (toolIdx.Count <= keepLastToolResults) return 0;

        // Everything at or after this index is kept verbatim (the freshest results).
        var firstKept = toolIdx[^keepLastToolResults];

        var rebuilt = new List<ChatMessage>(msgs.Count);
        var truncated = 0;
        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m.Role == "tool" && i < firstKept
                && m.Content is { Length: > 0 } content && content.Length > perResultCap)
            {
                var elided = content.Length - perResultCap;
                var shortened = content[..perResultCap] +
                    $"\n… [truncated {elided} chars mid-task to free context]";
                rebuilt.Add(m with { Content = shortened });
                truncated++;
            }
            else
            {
                rebuilt.Add(m);
            }
        }

        if (truncated == 0) return 0;

        session.Compact(rebuilt);
        // The next server response will recount from a smaller baseline; reset our tracker so we
        // don't keep reporting "context full" until it does.
        LastPromptTokens = 0;
        return truncated;
    }

    /// <summary>
    /// Visible for tests. Splits the message list into (system+, summarizable, tail).
    /// system+: every message before the first user turn (typically just one system message);
    /// summarizable: the messages from the first user turn up to but not including the start
    ///   of the last K user turns;
    /// tail: the last K user turns and everything after them.
    /// If the session has fewer than K+1 user turns the summarizable slice is empty.
    /// </summary>
    internal static (IReadOnlyList<ChatMessage> Head, IReadOnlyList<ChatMessage> Body, IReadOnlyList<ChatMessage> Tail)
        Slice(IReadOnlyList<ChatMessage> msgs)
    {
        var userIndices = new List<int>();
        for (var i = 0; i < msgs.Count; i++)
            if (msgs[i].Role == "user") userIndices.Add(i);

        if (userIndices.Count <= KeepLastUserTurns)
        {
            // Not enough turns to compact yet — body is empty, tail is everything.
            return (Array.Empty<ChatMessage>(), Array.Empty<ChatMessage>(), msgs);
        }

        var firstUserIdx = userIndices[0];
        var firstKeptUserIdx = userIndices[^KeepLastUserTurns];

        var head = msgs.Take(firstUserIdx).ToList();
        var body = msgs.Skip(firstUserIdx).Take(firstKeptUserIdx - firstUserIdx).ToList();
        var tail = msgs.Skip(firstKeptUserIdx).ToList();
        return (head, body, tail);
    }

    private async Task<string> SummarizeAsync(IReadOnlyList<ChatMessage> body, LiteLLMClient client, CancellationToken ct)
    {
        var rendered = RenderForSummary(body);
        var summaryRequest = new[]
        {
            ChatMessage.System(CompactionPrompt),
            ChatMessage.User(rendered),
        };
        return await client.GetCompletionAsync(summaryRequest, MediumModel, ct).ConfigureAwait(false);
    }

    private static string RenderForSummary(IReadOnlyList<ChatMessage> msgs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in msgs)
        {
            sb.Append('[').Append(m.Role).Append(']').AppendLine();
            if (!string.IsNullOrEmpty(m.Content)) sb.AppendLine(m.Content);
            if (!m.ToolCalls.IsDefaultOrEmpty)
            {
                foreach (var tc in m.ToolCalls)
                    sb.Append("  → ").Append(tc.FunctionName).Append('(').Append(tc.Arguments).Append(')').AppendLine();
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
