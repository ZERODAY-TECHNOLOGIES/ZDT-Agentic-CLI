using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
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
        => EstimateSessionTokens(session) >= ContextWindow * HardThreshold;

    /// <summary>
    /// Approximate total token count for the whole session (sum of <see cref="EstimateTokensByRole"/>,
    /// at 4 chars/token). Used to measure how much a compaction pass freed and to project the next
    /// prompt's size independently of the server's last reported <see cref="LastPromptTokens"/>
    /// (which is reset to 0 right after a compaction).
    /// </summary>
    public static int EstimateSessionTokens(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var total = 0;
        foreach (var (_, t) in EstimateTokensByRole(session)) total += t;
        return total;
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

        // Pull the system content, stripping any <conversation_summary> a PRIOR compaction folded in.
        // Without this, every compaction would APPEND a fresh ~500-word summary block and the system
        // prompt would grow unbounded across a long session — the "context creeps up after each compact"
        // bug. The prior summary is NOT lost: it is fed to the summariser as the story-so-far so the
        // single new block stays cumulative but bounded (the prompt caps it at ~500 words).
        var systemContents = systemPart
            .Where(m => m.Role == "system" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content!)
            .ToList();
        var priorSummary = StripSummaryBlocks(systemContents);

        var summary = await SummarizeAsync(summarizable, client, priorSummary, ct).ConfigureAwait(false);

        // Fold the summary into a SINGLE leading system message. Chat templates for Qwen3.x
        // (and GLM via vLLM) hard-require the only system message to be first and raise
        // "System message must be at the beginning" for any other — so the summary must NOT be
        // its own second system message, or every turn after compaction 400s. Concatenate the
        // original system prompt with the (single) <conversation_summary> block instead.
        var summaryBlock = $"<conversation_summary>\n{summary.Trim()}\n</conversation_summary>";
        var systemText = string.Join("\n\n", systemContents.Append(summaryBlock));

        var rebuilt = new List<ChatMessage>(1 + tail.Count) { ChatMessage.System(systemText) };
        // Preserve any non-system head messages (there are none in a normal session, whose head
        // is just the system prompt) after the single system line.
        foreach (var m in systemPart)
            if (m.Role != "system") rebuilt.Add(m);
        rebuilt.AddRange(tail);

        session.Compact(rebuilt);
        // After compaction the next turn's prompt_tokens count will redo from a much smaller
        // baseline; reset our tracker so we don't keep shouting "context full" until the next
        // server response updates it.
        LastPromptTokens = 0;
        return summarizable.Count;
    }

    /// <summary>
    /// Free as much context as possible for the current session and return the estimated number of
    /// tokens freed. First <see cref="CompactAsync"/> (summarise whole past user turns), then trim
    /// old tool results in place with ESCALATING aggressiveness (keep fewer verbatim, cap smaller)
    /// until under the hard threshold or nothing is left to trim.
    ///
    /// <para>
    /// The escalation is the fix for the single-long-turn loop: summarisation is a no-op when there
    /// is only one user message, and a single fixed-cap truncation pass often can't get under the
    /// threshold, so the old auto-compact printed the same warning every iteration while the context
    /// never shrank. Returns 0 when nothing could be freed (the bulk lives in the newest results we
    /// keep verbatim, or in assistant text, neither of which this touches).
    /// </para>
    /// </summary>
    public async Task<int> CompactToFitAsync(Session session, LiteLLMClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);

        var before = EstimateSessionTokens(session);
        await CompactAsync(session, client, ct).ConfigureAwait(false);
        foreach (var (keep, cap) in new[] { (3, 2000), (2, 1000), (1, 500) })
        {
            if (!IsProjectedBeyondHardThreshold(session)) break;
            TruncateOldToolResults(session, keep, cap);
        }
        return Math.Max(0, before - EstimateSessionTokens(session));
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

        // Index of every tool-result message, in order. Covers BOTH tool-calling shapes: the native
        // `tool` role, and XML mode's synthetic `user` turn framed as "EXECUTION RESULT of [Tool]:".
        // Without the XML case a tool-heavy XML-mode session accumulates tool output (as user turns) that
        // this pass can't touch, and the context window blows (a request hit ~261k tokens at the limit).
        var toolIdx = new List<int>();
        for (var i = 0; i < msgs.Count; i++)
            if (IsToolResultMessage(msgs[i])) toolIdx.Add(i);

        if (toolIdx.Count <= keepLastToolResults) return 0;

        // Everything at or after this index is kept verbatim (the freshest results).
        var firstKept = toolIdx[^keepLastToolResults];

        var rebuilt = new List<ChatMessage>(msgs.Count);
        var truncated = 0;
        for (var i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (IsToolResultMessage(m) && i < firstKept
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
    /// A message holding tool output that <see cref="TruncateOldToolResults"/> may shorten: either the
    /// native <c>tool</c> role, or XML mode's representation — a synthetic <c>user</c> turn whose content
    /// begins with <c>EXECUTION RESULT of [</c>. A real user message never starts with that literal, so
    /// the check can't misfire on genuine prompts.
    /// </summary>
    private static bool IsToolResultMessage(ChatMessage m) =>
        m.Role == "tool"
        || (m.Role == "user" && m.Content is { Length: > 0 } c
            && c.StartsWith("EXECUTION RESULT of [", StringComparison.Ordinal));

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

    private static readonly Regex SummaryBlock = new(
        @"<conversation_summary>\s*(.*?)\s*</conversation_summary>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Remove every <c>&lt;conversation_summary&gt;</c> block a prior compaction folded into the system
    /// content (a build that stacked them may have several) and return their combined inner text, so the
    /// caller carries it into the NEW summary instead of re-appending — keeping exactly one block. Mutates
    /// <paramref name="systemContents"/> in place; entries left empty after stripping are dropped.
    /// </summary>
    private static string StripSummaryBlocks(List<string> systemContents)
    {
        var prior = new StringBuilder();
        for (var i = 0; i < systemContents.Count; i++)
        {
            var content = systemContents[i];
            var matches = SummaryBlock.Matches(content);
            if (matches.Count == 0) continue;
            foreach (Match m in matches)
            {
                if (prior.Length > 0) prior.Append("\n\n");
                prior.Append(m.Groups[1].Value.Trim());
            }
            systemContents[i] = SummaryBlock.Replace(content, string.Empty).Trim();
        }
        systemContents.RemoveAll(string.IsNullOrEmpty);
        return prior.ToString();
    }

    private async Task<string> SummarizeAsync(
        IReadOnlyList<ChatMessage> body, LiteLLMClient client, string priorSummary, CancellationToken ct)
    {
        var rendered = RenderForSummary(body);
        // Carry a prior summary forward as the story-so-far so repeated compaction stays cumulative
        // (one bounded block) instead of stacking blocks or losing older history.
        var userContent = string.IsNullOrWhiteSpace(priorSummary)
            ? rendered
            : "Summary of the conversation SO FAR (compacted earlier — fold it into your new summary so " +
              "nothing is lost):\n" + priorSummary + "\n\n--- newer conversation to fold in ---\n" + rendered;
        var summaryRequest = new[]
        {
            ChatMessage.System(CompactionPrompt),
            ChatMessage.User(userContent),
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
