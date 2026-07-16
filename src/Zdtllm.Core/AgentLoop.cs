using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using Zdtllm.Core.Sessions;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core;

public sealed record AgentLoopOptions
{
    public const string DefaultSystemPrompt =
        "You are zdtllmcli, an autonomous CLI assistant from zer0day.ro. " +
        "Use the provided tools to read files and run shell commands when needed. " +
        "Be concise and prefer concrete answers over speculation.";

    public required string Model { get; init; }
    /// <summary>
    /// Cap on agent loop iterations. Defaults to <see cref="int.MaxValue"/> — effectively
    /// no limit, matching claude-cli's "no default cap" behaviour. Tests and CI scripts
    /// that need a hard ceiling pass an explicit value (the CLI flag is <c>--max-turns</c>).
    /// </summary>
    public int MaxTurns { get; init; } = int.MaxValue;
    public bool SkipPermissions { get; init; }
    public ToolCallingMode ToolCallingMode { get; init; } = ToolCallingMode.Native;
    public string SystemPrompt { get; init; } = DefaultSystemPrompt;

    /// <summary>
    /// Cap on concurrent tool executions when a batch is parallel-eligible. 0 or
    /// negative means "no cap" (the historical Task.WhenAll behaviour). The cap
    /// matters mainly for Task-tool fan-out: each parallel subagent triggers its
    /// own LiteLLM stream, and most proxies enforce per-key rate limits.
    /// </summary>
    public int MaxParallel { get; init; } = 0;
}

public sealed record AgentResult(
    string FinalText,
    int Turns,
    int? PromptTokens,
    int? CompletionTokens);

public sealed class AgentLoop
{
    private static readonly Color BrandCyan = new(0x1B, 0xEA, 0xCD);
    private static readonly Color BrandGold = new(0xE5, 0xD9, 0x36);
    private static readonly Color MuteText = new(0x68, 0x7B, 0x89);

    private readonly LiteLLMClient _client;
    private readonly ToolRegistry _tools;
    private readonly PermissionRuleSet _perms;
    private readonly AgentLoopOptions _options;
    private readonly ContextManager? _context;
    private readonly IAnsiConsole? _richConsole;
    private readonly IAgentObserver? _observer;

    /// <summary>
    /// Optional queue of user messages typed while THIS turn is already running (interactive
    /// REPL only). Drained between tool rounds so a queued follow-up is folded into the ongoing
    /// task rather than waiting for the whole turn to finish. Null for print mode, subagents,
    /// and tests — they never have a live human typing mid-turn.
    /// </summary>
    private readonly IUserInputQueue? _inputQueue;

    /// <summary>
    /// Optional plan-mode switch. When on, mutating tools are blocked and each user prompt is
    /// grounded with a plan-mode reminder, so the agent researches + drafts a plan (and calls
    /// ExitPlanMode) instead of changing the workspace. Null (the common case) disables all of it.
    /// </summary>
    private readonly IPlanModeSwitch? _planMode;

    /// <summary>
    /// Optional view of the user's mid-turn typing, surfaced in the live spinner so queued input
    /// is visible instead of feeling like the terminal froze. Null in print mode / tests.
    /// </summary>
    private readonly ITypeAheadStatus? _typeAhead;

    /// <summary>
    /// Per-turn count of tool calls that returned <c>isError=true</c> (unknown tool,
    /// permission denied, JSON parse failure, tool's own thrown exception, etc.). Reset
    /// at the top of every <see cref="RunTurnAsync"/> call and surfaced via
    /// <see cref="IAgentObserver.OnResultAsync"/> so consumers can distinguish
    /// "model deliberately ended after a clean run" from "model ended after every tool
    /// call failed and it gave up." Subagents have their own <see cref="AgentLoop"/>
    /// instance, so their counter is independent from the parent's.
    /// Mutated under <see cref="Interlocked"/> because parallel tool batches run on
    /// the thread pool.
    /// </summary>
    private int _turnToolErrorCount;

    /// <summary>
    /// Rolling window of recent tool-call fingerprints used by the loop detector.
    /// Sized so 2-3 productive calls naturally evict any one entry, but a model stuck
    /// in a 5-pattern rotation still triggers detection. Reset per <see cref="RunTurnAsync"/> —
    /// loops don't span turns because each turn gets a fresh REPL prompt anyway.
    /// All access goes through <see cref="_recentToolCallsLock"/>: tool dispatch can be
    /// parallel (<c>MaxParallel &gt; 1</c>) and <see cref="Queue{T}"/> isn't thread-safe.
    /// </summary>
    private readonly Queue<ToolCallTrace> _recentToolCalls = new();
    private readonly object _recentToolCallsLock = new();

    /// <summary>
    /// Counts loop-break warnings the model has received without changing strategy.
    /// Resets the moment a tool call returns a result genuinely different from recent
    /// same-tool calls. Once it reaches <see cref="MaxConsecutiveBreaks"/>, every
    /// subsequent break message is suffixed with a stronger "stop calling tools, write
    /// your final response" directive — there's no hard enforcement layer (the model
    /// CAN still emit tool calls), but the message escalates from advisory to
    /// imperative. This is a known limitation: a stubborn model will get the same
    /// final message printed repeatedly until <c>MaxTurns</c> kicks in.
    /// </summary>
    private int _consecutiveLoopBreaks;

    /// <summary>Window size for the rolling buffer of tool-call fingerprints.</summary>
    private const int LoopWindowSize = 10;

    /// <summary>
    /// Number of identical-args + identical-result calls in the window before pre-execute
    /// short-circuit fires. Threshold of 3 (block on the 3rd call) gives the model a free
    /// retry — calling the same tool twice with the same args is plausible (verify-after-edit
    /// path); calling it three times is loopy.
    /// </summary>
    private const int ExactRepeatThreshold = 3;

    /// <summary>
    /// Number of same-tool + same-result-hash calls (with permuted args) before the
    /// post-execute warning fires on a search tool. Threshold of 3 matches the canonical
    /// "Grep three different patterns, all return (no matches)" loop.
    /// </summary>
    private const int SameResultThreshold = 3;

    /// <summary>
    /// Once <see cref="_consecutiveLoopBreaks"/> reaches this, subsequent break messages
    /// include the stronger "stop calling tools" directive.
    /// </summary>
    private const int MaxConsecutiveBreaks = 3;

    /// <summary>
    /// Permutation detection (same tool, same result, different args) only fires for
    /// search tools where "tried 3 patterns, all return identical (no matches)" is a
    /// genuine diagnostic signal. For Read/Bash/WebFetch/Edit/Write, legitimate same-result
    /// patterns happen (reading 8 stub Migration_*.cs files all return similar headers,
    /// `git status` returns the same string twice when nothing changed) and a permutation
    /// warning would block productive work. Add new tools here only after confirming
    /// "different args, same result" is a loop indicator and not a normal pattern.
    /// </summary>
    private static readonly HashSet<string> SearchToolsForPermutationCheck = new(StringComparer.Ordinal)
    {
        "Grep",
        "Glob",
        "WebSearch",
    };

    /// <summary>
    /// Fingerprint of a recent tool call. <see cref="ResultHash"/> is the literal string
    /// <c>"loop-break"</c> for short-circuited calls so the buffer slot still occupies
    /// space (preventing the model from oscillating between two patterns and never
    /// triggering threshold) but doesn't collide with any real result hash.
    /// </summary>
    private readonly record struct ToolCallTrace(string Tool, string ArgsHash, string ResultHash);

    private const string ShortCircuitedResultMarker = "loop-break";

    /// <summary>Compiled once — used by <see cref="HashResult"/> to collapse cosmetic whitespace differences.</summary>
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public AgentLoop(
        LiteLLMClient client,
        ToolRegistry tools,
        PermissionRuleSet perms,
        AgentLoopOptions options,
        ContextManager? context = null,
        IAnsiConsole? richConsole = null,
        IAgentObserver? observer = null,
        IUserInputQueue? inputQueue = null,
        IPlanModeSwitch? planMode = null,
        ITypeAheadStatus? typeAhead = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(perms);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _tools = tools;
        _perms = perms;
        _options = options;
        _context = context;
        _richConsole = richConsole;
        _observer = observer;
        _inputQueue = inputQueue;
        _planMode = planMode;
        _typeAhead = typeAhead;
    }

    public PermissionRuleSet Permissions => _perms;
    public ToolRegistry Tools => _tools;
    public LiteLLMClient Client => _client;
    public ContextManager? Context => _context;
    public AgentLoopOptions Options => _options;
    public IAnsiConsole? RichConsole => _richConsole;
    public IAgentObserver? Observer => _observer;

    /// <summary>
    /// Backwards-compatible one-shot entry point: spins up an ephemeral
    /// (non-persistent) session, runs a single user→assistant exchange, and
    /// returns the final answer. Equivalent to creating Session.NewEphemeral
    /// and calling RunTurnAsync.
    /// </summary>
    public Task<AgentResult> RunOneShotAsync(
        string userPrompt,
        TextWriter output,
        TextWriter status,
        CancellationToken ct = default)
    {
        var session = Session.NewEphemeral(_options.Model, _options.ToolCallingMode);
        return RunTurnAsync(session, userPrompt, output, status, ct);
    }

    /// <summary>
    /// Runs a single user turn against the given session. Mutates the session
    /// (adds the user message, the assistant response, and any tool calls /
    /// results that occurred). On a fresh session this also bootstraps the
    /// system prompt — subsequent turns reuse the existing one. The session's
    /// Model and Mode are authoritative; AgentLoopOptions are only consulted
    /// for ephemeral session bootstrap and for system-prompt content.
    /// </summary>
    public async Task<AgentResult> RunTurnAsync(
        Session session,
        string userPrompt,
        TextWriter output,
        TextWriter status,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(userPrompt);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(status);

        var xmlMode = session.Mode == ToolCallingMode.Xml;

        // Reset the per-turn tool-error counter so a previous turn's failures don't bleed
        // into this one's result event. Subagents have their own AgentLoop instance, so
        // this only resets for "this" agent — the parent's counter is untouched while a
        // subagent runs.
        Interlocked.Exchange(ref _turnToolErrorCount, 0);

        // Loop detector also resets per-turn — a fresh REPL prompt represents a fresh
        // intent and shouldn't inherit a previous turn's "stuck" state. The buffer is
        // local to this AgentLoop instance, so subagents have their own.
        Interlocked.Exchange(ref _consecutiveLoopBreaks, 0);
        lock (_recentToolCallsLock) _recentToolCalls.Clear();

        // Bootstrap system prompt the first time the session is touched.
        if (session.Messages.Count == 0)
        {
            var systemPrompt = xmlMode
                ? BuildXmlSystemPrompt(_options.SystemPrompt, _tools.Schemas)
                : _options.SystemPrompt;
            session.AddSystem(systemPrompt);
        }

        // Plan mode grounding: fold a reminder into the user turn so any model — however long the
        // context has grown — keeps behaving read-only until the user approves a plan. The
        // hard guarantee is the tool-dispatch block below; this just keeps the model cooperative.
        var effectivePrompt = _planMode?.InPlanMode == true
            ? userPrompt + "\n\n" + PlanModeState.Reminder
            : userPrompt;
        session.AddUser(effectivePrompt);

        IReadOnlyList<ToolDef>? toolDefList = null;
        if (!xmlMode)
        {
            var defs = _tools.Schemas
                .Select(s => new ToolDef(s.Name, s.Description, s.Parameters))
                .ToList();
            toolDefList = defs.Count > 0 ? defs : null;
        }

        // Model on ToolContext intentionally tracks session.Model (which /model mutates), not
        // _options.Model (frozen at startup). Tools that fan out further work — TaskTool
        // dispatching a subagent in particular — need the CURRENT model so a mid-conversation
        // /model switch reaches downstream agents instead of stranding them on the original.
        var ctx = new ToolContext(Cwd: Directory.GetCurrentDirectory(), Model: session.Model);
        int? lastPromptTokens = null;
        int? lastCompletionTokens = null;
        // Running totals across all iterations of THIS turn — fed to OnResultAsync so the
        // claude-shaped result event can publish summed billed tokens for the whole exchange.
        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        // Flips on the first turn where xmlMode is active, no calls got extracted, and the
        // assistant text contains XML markup that looks corrupted (close tag without open,
        // stray invoke/function markers). Surfaced via observer hooks so consumers like
        // AppSec-Automator can branch on it without grepping result.text.
        bool formatBreakdownDetected = false;

        try
        {
        for (var turn = 1; turn <= _options.MaxTurns; turn++)
        {
            var assistantText = new StringBuilder();
            var pending = new SortedDictionary<int, ToolCallAccumulator>();
            int? turnPromptTokens = null;
            int? turnCompletionTokens = null;
            // Char count of reasoning_content seen this turn (DeepSeek V3.x and other
            // reasoning models). Counted only for verbose telemetry — the actual text
            // is dropped per spec (reasoning is ephemeral, must not feed back into context).
            var reasoningCharsThisTurn = 0;

            // Live spinner counters: number of characters streamed (for a tokens-approximation
            // since servers don't send incremental usage), and total chunks (debug-style metric).
            // The Stopwatch is the live "elapsed" the spinner shows.
            var streamSw = System.Diagnostics.Stopwatch.StartNew();
            var charsStreamed = 0;
            var lastSpinnerUpdate = TimeSpan.Zero;

            async Task ConsumeStreamAsync(StatusContext? statusCtx)
            {
                // Show an initial label immediately so the user sees "thinking (0s)" instead of
                // a static "thinking" until the first chunk arrives — for slow-thinking models
                // the gap to first chunk can be 30s+, and a frozen label looks broken.
                statusCtx?.Status(BuildSpinnerLabel(streamSw.Elapsed, charsStreamed));

                // Periodic ticker: refresh the spinner even when no chunks arrive, so the elapsed
                // counter advances (a frozen "0s" looks broken on a slow backend) AND the
                // type-ahead readout (what the user is typing / how many messages are queued)
                // stays live. 120ms keeps mid-turn typing feeling responsive; when there's no
                // type-ahead source, a slower cadence is plenty for just the clock.
                var tickMs = _typeAhead is not null ? 120 : 400;
                using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tickerTask = statusCtx is null
                    ? Task.CompletedTask
                    : Task.Run(async () =>
                    {
                        try
                        {
                            while (!tickerCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(tickMs, tickerCts.Token).ConfigureAwait(false);
                                statusCtx.Status(BuildSpinnerLabel(streamSw.Elapsed, charsStreamed));
                            }
                        }
                        catch (OperationCanceledException) { /* normal shutdown */ }
                    }, tickerCts.Token);

                try
                {
                await foreach (var chunk in _client.StreamChatAsync(session.Messages, toolDefList, session.Model, ct).ConfigureAwait(false))
                {
                    switch (chunk)
                    {
                        case ChatChunk.TextDelta td:
                            assistantText.Append(td.Text);
                            charsStreamed += td.Text.Length;
                            // Observer always sees raw deltas — that's what stream-json wants. The
                            // text writer / rich console split below is purely for the human-facing
                            // terminal, which the observer pipeline doesn't go through.
                            await SafeNotifyAsync(_observer?.OnTextDeltaAsync(td.Text, ct)).ConfigureAwait(false);
                            // Rich console suppresses per-delta writes — markdown gets rendered as one block
                            // once the stream completes (or just before tool dispatch). Keeps terminal clean
                            // while the thinking spinner runs.
                            if (!xmlMode && _richConsole is null)
                            {
                                await output.WriteAsync(td.Text.AsMemory(), ct).ConfigureAwait(false);
                                await output.FlushAsync(ct).ConfigureAwait(false);
                            }
                            UpdateSpinnerThrottled(statusCtx, streamSw, charsStreamed, ref lastSpinnerUpdate);
                            break;

                        case ChatChunk.ReasoningDelta rd:
                            // Drop reasoning_content from assistantText, observers, output, and
                            // session history — it's chain-of-thought, ephemeral by spec. Bump the
                            // streamed-char counter so the spinner keeps advancing during a long
                            // think (otherwise reasoning models look frozen for tens of seconds).
                            reasoningCharsThisTurn += rd.Text.Length;
                            charsStreamed += rd.Text.Length;
                            UpdateSpinnerThrottled(statusCtx, streamSw, charsStreamed, ref lastSpinnerUpdate);
                            break;

                        case ChatChunk.ToolCallDelta tcd:
                            if (!pending.TryGetValue(tcd.Index, out var acc))
                                pending[tcd.Index] = acc = new ToolCallAccumulator();
                            if (tcd.Id is not null) acc.Id = tcd.Id;
                            if (tcd.FunctionName is not null) acc.FunctionName = tcd.FunctionName;
                            if (tcd.ArgumentsDelta is not null) acc.Arguments.Append(tcd.ArgumentsDelta);
                            // Tool-call deltas have args text we can use for the char counter too —
                            // gives "thinking" a visible heartbeat even on tool-call turns where
                            // there's no assistant text yet.
                            charsStreamed += tcd.ArgumentsDelta?.Length ?? 0;
                            UpdateSpinnerThrottled(statusCtx, streamSw, charsStreamed, ref lastSpinnerUpdate);
                            break;

                        case ChatChunk.Usage u:
                            turnPromptTokens = u.PromptTokens;
                            turnCompletionTokens = u.CompletionTokens;
                            lastPromptTokens = u.PromptTokens;
                            lastCompletionTokens = u.CompletionTokens;
                            break;

                        case ChatChunk.Done:
                            break;
                    }
                }
                }
                finally
                {
                    // Always stop the ticker — without this, a thrown exception inside the await
                    // foreach would leak the background task. Wait briefly for it to observe the
                    // cancellation; swallow on timeout (the task is harmless if it lingers).
                    tickerCts.Cancel();
                    try { await tickerTask.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
                    catch { /* swallow */ }
                }
            }

            if (_richConsole is not null)
            {
                // Spectre's Status() can periodically refresh the label without us repainting —
                // we still call ctx.Status(...) on each chunk so the elapsed counter advances
                // visibly even between text deltas.
                await _richConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(new Style(BrandCyan))
                    .StartAsync(BuildSpinnerLabel(TimeSpan.Zero, 0), async ctx => await ConsumeStreamAsync(ctx))
                    .ConfigureAwait(false);
            }
            else
            {
                await ConsumeStreamAsync(null).ConfigureAwait(false);
            }

            if (turnPromptTokens is int p && turnCompletionTokens is int c)
            {
                session.AddUsage(p, c);
                _context?.RegisterTurn(p, c);
                totalInputTokens += p;
                totalOutputTokens += c;
            }

            // GLM-5 (and likely other Ollama-Cloud models trained with looser tool-use data)
            // sometimes emit "parallel" calls by concatenating N JSON arg objects into a single
            // tool_call entry: {"x":1}{"y":2}{"z":3}. The downstream tool would reject the
            // string as malformed JSON, and LiteLLM rejects the next request when we echo it
            // back as history. SplitConcatenatedArgs detects this exact pattern (parse fails
            // with "Extra data") and rewrites one ToolCall into N — single-object args
            // bypass the path entirely so non-buggy models are unaffected.
            var nativeCalls = pending.Values
                .Where(v => v.Id is not null && v.FunctionName is not null)
                .Select(v => new ToolCall(v.Id!, v.FunctionName!, v.Arguments.ToString()))
                .SelectMany(c => SplitConcatenatedArgs(c, status))
                .ToImmutableArray();

            IReadOnlyList<ParsedXmlToolCall> xmlCalls = nativeCalls.Length == 0 && xmlMode
                ? XmlToolCallParser.ExtractCalls(assistantText.ToString())
                : [];

            // Format-breakdown detection: model produced XML-shaped markup but the strict
            // parser found 0 calls and the recovery path also failed. Almost always means an
            // upstream proxy/chat template chewed the open tag (we still see </function_calls>
            // but no <function_calls> or its corruption). Fire the warning hook so stream-json
            // consumers see it immediately, set the flag for the eventual result event, and
            // print a one-time stderr note for human users on this run.
            if (xmlMode && xmlCalls.Count == 0
                && !formatBreakdownDetected
                && XmlToolCallParser.LooksLikeBrokenXml(assistantText.ToString()))
            {
                formatBreakdownDetected = true;
                var breakdownDetails =
                    "assistant emitted XML tool-call markup but the open tag was malformed " +
                    "(close tag with no matching open, or stray <invoke>/<function=> marker). " +
                    "Upstream proxy/chat template likely stripped bytes; check LiteLLM config " +
                    "or try --tool-calling native if the model supports it.";
                await status.WriteLineAsync(Palette.Red("  ⚠ format breakdown:") + " " + Palette.Mute(breakdownDetails))
                    .ConfigureAwait(false);
                await SafeNotifyAsync(_observer?.OnFormatBreakdownAsync(breakdownDetails, ct))
                    .ConfigureAwait(false);
            }

            // Surface XML-extracted calls as ToolCall items in the assistant event so
            // stream-json consumers (AppSec-Automator) get the same {type:"tool_use",...}
            // blocks they would for native mode. The id format must match what
            // ExecuteXmlRoundAsync uses below so any later tool_result correlates.
            var observableCalls = nativeCalls.Length > 0
                ? nativeCalls
                : xmlCalls.Count > 0
                    ? xmlCalls
                        .Select((c, i) => new ToolCall($"xml_{turn}_{i}", c.FunctionName, c.ArgumentsJson))
                        .ToImmutableArray()
                    : ImmutableArray<ToolCall>.Empty;

            // For XML mode, strip the raw <function_calls> markup out of the text we hand to
            // observers — the data is already represented by tool_use blocks, and emitting the
            // unparsed XML alongside duplicates the payload and confuses Anthropic-shaped consumers.
            var observableText = xmlMode
                ? XmlToolCallParser.Strip(assistantText.ToString()).TrimEnd()
                : assistantText.ToString();

            // Reasoning telemetry — writes only to the status channel (--verbose path), never
            // to the model-facing output or session history. Helps debug "model thought 30s
            // and produced nothing observable" cases on DeepSeek/R1-style models.
            if (reasoningCharsThisTurn > 0)
            {
                await status.WriteLineAsync(
                    Palette.Mute($"  ↳ reasoning: {reasoningCharsThisTurn} chars (dropped from context)"))
                    .ConfigureAwait(false);
            }

            // Notify observers about the assistant message that just streamed (text + tool
            // calls + per-turn usage). For stream-json mode this is the per-iteration
            // {"type":"assistant",...} event AppSec-Automator scans for billed-token totals.
            await SafeNotifyAsync(_observer?.OnAssistantTurnAsync(
                observableText,
                observableCalls,
                session.Model,
                turnPromptTokens,
                turnCompletionTokens,
                ct)).ConfigureAwait(false);

            if (nativeCalls.Length == 0 && xmlCalls.Count == 0)
            {
                var displayText = xmlMode
                    ? XmlToolCallParser.Strip(assistantText.ToString()).TrimEnd()
                    : assistantText.ToString();

                // Reasoning-only completion: model emitted chain-of-thought but no content
                // and no tool calls. Common with mis-configured DeepSeek-R1 / V3.x deployments
                // where the proxy forces thinking mode for every turn. Surface this clearly so
                // users don't think the binary swallowed their answer.
                if (displayText.Length == 0 && reasoningCharsThisTurn > 0)
                {
                    await status.WriteLineAsync(Palette.Mute(
                        "  ↳ model emitted reasoning_content only — no observable text or tool calls. " +
                        "Try a non-reasoning variant of this model."))
                        .ConfigureAwait(false);
                }

                if (_richConsole is not null && displayText.Length > 0)
                {
                    // Rich path: text was buffered (not streamed). Render as markdown now.
                    _richConsole.Write(MarkdownRenderer.Render(displayText));
                    _richConsole.WriteLine();
                }
                else
                {
                    if (xmlMode && displayText.Length > 0)
                        await output.WriteAsync(displayText.AsMemory(), ct).ConfigureAwait(false);

                    if (assistantText.Length > 0)
                        await output.WriteLineAsync().ConfigureAwait(false);
                }

                session.AddAssistant(
                    content: displayText.Length > 0 ? displayText : null,
                    toolCalls: ImmutableArray<ToolCall>.Empty);

                await SafeNotifyAsync(_observer?.OnFinalAsync(
                    displayText, turn, lastPromptTokens, lastCompletionTokens, ct)).ConfigureAwait(false);
                await SafeNotifyAsync(_observer?.OnResultAsync(
                    subtype: "success",
                    isError: false,
                    numTurns: turn,
                    stopReason: "end_turn",
                    resultText: displayText,
                    totalInputTokens: totalInputTokens,
                    totalOutputTokens: totalOutputTokens,
                    ct: ct,
                    formatBreakdown: formatBreakdownDetected,
                    toolErrorCount: Volatile.Read(ref _turnToolErrorCount))).ConfigureAwait(false);
                return new AgentResult(displayText, turn, lastPromptTokens, lastCompletionTokens);
            }

            if (nativeCalls.Length > 0)
            {
                await ExecuteNativeRoundAsync(session, assistantText, nativeCalls, ctx, output, status, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await ExecuteXmlRoundAsync(session, assistantText, xmlCalls, turn, ctx, output, status, ct)
                    .ConfigureAwait(false);
            }

            // Mid-turn auto-compact: if the just-finished iteration OR the just-appended tool
            // results pushed us past the hard threshold, summarise older history before the next
            // iteration sends an even bigger context.
            //   - IsBeyondHardThreshold uses the LAST prompt_tokens from the server (only counts
            //     through the assistant message);
            //   - IsProjectedBeyondHardThreshold estimates the CURRENT session size including
            //     tool results we just appended — catches the "Read 2 huge files → next prompt
            //     blows the limit" case that the per-turn counter alone misses.
            // This is the only path that fires inside a subagent (subagents have their own
            // ContextManager and never hit the pre-prompt path that the parent's REPL might run).
            if (_context is not null
                && (_context.IsBeyondHardThreshold || _context.IsProjectedBeyondHardThreshold(session)))
            {
                await status.WriteLineAsync(
                    Palette.Red($"[auto-compact at {_context.UsagePercent}%]") + " " +
                    Palette.Mute("summarising older turns mid-task to free context"))
                    .ConfigureAwait(false);
                try
                {
                    await _context.CompactAsync(session, _client, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await status.WriteLineAsync(Palette.Red($"auto-compact failed: {ex.Message}"))
                        .ConfigureAwait(false);
                }
            }
        }

        // Loop fell through without an explicit return — the model kept asking for tools
        // for too many iterations. Notify observers of the error_max_turns terminus
        // (claude-cli equivalent) and surface it to the caller as an exception so REPL/print
        // mode can decide what to do (print + exit non-zero, or continue the REPL).
        await SafeNotifyAsync(_observer?.OnResultAsync(
            subtype: "error_max_turns",
            isError: true,
            numTurns: _options.MaxTurns,
            stopReason: "max_turns",
            resultText: null,
            totalInputTokens: totalInputTokens,
            totalOutputTokens: totalOutputTokens,
            ct: CancellationToken.None,
            formatBreakdown: formatBreakdownDetected,
            toolErrorCount: Volatile.Read(ref _turnToolErrorCount))).ConfigureAwait(false);

        throw new InvalidOperationException(
            $"Agent exceeded max turns ({_options.MaxTurns}) without completing.");
        }
        catch (RateLimitException rl)
        {
            // Surface the structured rate-limit signal BEFORE the terminal result event so a
            // stream-json consumer can parse the resetsAt hint and schedule a retry. Then
            // emit the result-event terminus (is_error=true, stop_reason=rate_limited) so
            // the consumer always sees exactly one terminal event regardless of the cause.
            await SafeNotifyAsync(_observer?.OnRateLimitedAsync(
                status: "rejected",
                resetsAtUnix: rl.ResetsAtUnix,
                ct: CancellationToken.None)).ConfigureAwait(false);
            await SafeNotifyAsync(_observer?.OnResultAsync(
                subtype: "error_during_execution",
                isError: true,
                numTurns: 0,
                stopReason: "rate_limited",
                resultText: rl.Message,
                totalInputTokens: totalInputTokens,
                totalOutputTokens: totalOutputTokens,
                ct: CancellationToken.None,
                formatBreakdown: formatBreakdownDetected,
                toolErrorCount: Volatile.Read(ref _turnToolErrorCount))).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            // User-cancellation (Ctrl+C / programCts) — the existing flows in Repl/Program
            // print "(turn cancelled)" themselves, but a stream-json consumer still wants
            // a terminal result event so its parser doesn't hang waiting for one.
            await SafeNotifyAsync(_observer?.OnResultAsync(
                subtype: "error_during_execution",
                isError: true,
                numTurns: 0,
                stopReason: "cancelled",
                resultText: null,
                totalInputTokens: totalInputTokens,
                totalOutputTokens: totalOutputTokens,
                ct: CancellationToken.None,
                formatBreakdown: formatBreakdownDetected,
                toolErrorCount: Volatile.Read(ref _turnToolErrorCount))).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // Any other failure path — HTTP error, parse error, tool dispatch crash. Emit
            // error_during_execution so the consumer always gets a terminal event before
            // the exception escapes the loop.
            await SafeNotifyAsync(_observer?.OnResultAsync(
                subtype: "error_during_execution",
                isError: true,
                numTurns: 0,
                stopReason: null,
                resultText: null,
                totalInputTokens: totalInputTokens,
                totalOutputTokens: totalOutputTokens,
                ct: CancellationToken.None,
                formatBreakdown: formatBreakdownDetected,
                toolErrorCount: Volatile.Read(ref _turnToolErrorCount))).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteNativeRoundAsync(
        Session session,
        StringBuilder assistantText,
        ImmutableArray<ToolCall> calls,
        ToolContext ctx,
        TextWriter output,
        TextWriter status,
        CancellationToken ct)
    {
        session.AddAssistant(
            content: assistantText.Length > 0 ? assistantText.ToString() : null,
            toolCalls: calls);

        if (assistantText.Length > 0)
        {
            if (_richConsole is not null)
            {
                // Render the model's pre-toolcall narration as markdown before the tool spinner kicks in.
                _richConsole.Write(MarkdownRenderer.Render(assistantText.ToString()));
                _richConsole.WriteLine();
            }
            else
            {
                await output.WriteLineAsync().ConfigureAwait(false);
            }
        }

        var results = await DispatchToolCallsAsync(calls, ctx, status, ct).ConfigureAwait(false);
        for (var i = 0; i < calls.Length; i++)
            session.AddTool(calls[i].Id, results[i]);

        // Fold in any messages the user queued while this round ran. In native mode the history
        // now ends with tool messages, and a user message after tool results is valid — so we
        // add the queued text as its own user turn (all queued items combined into one to keep
        // strict-alternation templates happy). The model sees it on its very next call.
        var queued = DrainQueuedInput();
        if (queued is not null)
        {
            await status.WriteLineAsync(Palette.Cyan("  ↳ picked up your queued message") + " " +
                Palette.Mute(Truncate(queued.Replace('\n', ' '), 80))).ConfigureAwait(false);
            session.AddUser(queued);
        }
    }

    private async Task ExecuteXmlRoundAsync(
        Session session,
        StringBuilder assistantText,
        IReadOnlyList<ParsedXmlToolCall> xmlCalls,
        int turn,
        ToolContext ctx,
        TextWriter output,
        TextWriter status,
        CancellationToken ct)
    {
        var cleaned = XmlToolCallParser.Strip(assistantText.ToString()).Trim();
        if (cleaned.Length > 0)
        {
            if (_richConsole is not null)
            {
                _richConsole.Write(MarkdownRenderer.Render(cleaned));
                _richConsole.WriteLine();
            }
            else
            {
                await output.WriteAsync(cleaned.AsMemory(), ct).ConfigureAwait(false);
                await output.WriteLineAsync().ConfigureAwait(false);
            }
        }

        // Persist the original XML-bearing text so the model's own action survives
        // session resumes and feeds back into its context next turn.
        session.AddAssistant(content: assistantText.ToString(), toolCalls: ImmutableArray<ToolCall>.Empty);

        var calls = ImmutableArray.CreateBuilder<ToolCall>(xmlCalls.Count);
        for (var i = 0; i < xmlCalls.Count; i++)
        {
            var xml = xmlCalls[i];
            calls.Add(new ToolCall($"xml_{turn}_{i}", xml.FunctionName, xml.ArgumentsJson));
        }
        var callsArr = calls.ToImmutable();

        var results = await DispatchToolCallsAsync(callsArr, ctx, status, ct).ConfigureAwait(false);
        // Combine every tool result into a SINGLE synthetic user turn. Originally we did one
        // AddUser per call, but when the model emits N tool calls in one assistant turn the
        // session ends up with N consecutive user messages — a pattern Qwen3-Coder's chat
        // template rejects with the misleading vLLM error "System message must be at the
        // beginning". Strict alternation is the safest assumption for any open-source model
        // running through OpenAI-compat layers, so we collapse the batch unconditionally.
        // Native mode doesn't have this issue: tool messages live under their own role and
        // adjacency is allowed.
        var sb = new StringBuilder();
        for (var i = 0; i < callsArr.Length; i++)
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine("---").AppendLine();
            sb.Append("EXECUTION RESULT of [").Append(callsArr[i].FunctionName).Append("]:\n");
            sb.Append(results[i]);
        }

        // Fold in any queued user input. XML mode already collapses tool results into a single
        // synthetic user turn, so we append the queued text to the SAME message rather than a
        // second user turn — consecutive user messages break Qwen3-style chat templates.
        var queued = DrainQueuedInput();
        if (queued is not null)
        {
            await status.WriteLineAsync(Palette.Cyan("  ↳ picked up your queued message") + " " +
                Palette.Mute(Truncate(queued.Replace('\n', ' '), 80))).ConfigureAwait(false);
            sb.AppendLine().AppendLine().AppendLine("--- The user also sent this message: ---")
              .AppendLine().Append(queued);
        }

        session.AddUser(sb.ToString());
    }

    /// <summary>
    /// Run a batch of tool calls, parallelising via Task.WhenAll when every tool
    /// in the batch reports CanRunInParallel, otherwise serialising. Status lines
    /// are emitted before dispatch so the user sees both calls register up-front
    /// when running concurrently. Each call's result text is returned in the same
    /// order the calls were given so the caller can pair them with tool_call_ids.
    /// </summary>
    private async Task<string[]> DispatchToolCallsAsync(
        ImmutableArray<ToolCall> calls,
        ToolContext ctx,
        TextWriter status,
        CancellationToken ct)
    {
        if (calls.Length == 0) return Array.Empty<string>();

        var allParallelisable = calls.All(c =>
        {
            var tool = _tools.Get(c.FunctionName);
            return tool is not null && tool.CanRunInParallel;
        });

        // For parallel batches we always print the per-call status lines up-front (so the user
        // sees what is firing concurrently). For sequential single-tool dispatch with a rich
        // console, we suppress the static line and use a Status spinner instead. When an
        // observer is wired in (--verbose, --output-format) it owns the user-facing trace, so
        // we suppress the legacy status print to avoid duplicate lines.
        var useSpinnerPerCall = _richConsole is not null
            && (!allParallelisable || calls.Length == 1);

        if (!useSpinnerPerCall && _observer is null)
        {
            foreach (var call in calls)
            {
                await status.WriteLineAsync(FormatStatusLine(call.FunctionName, call.Arguments))
                    .ConfigureAwait(false);
            }
            if (allParallelisable && calls.Length > 1)
            {
                await status.WriteLineAsync(Palette.Mute($"  ↳ dispatching {calls.Length} calls in parallel"))
                    .ConfigureAwait(false);
            }
        }

        if (!allParallelisable || calls.Length == 1)
        {
            var sequential = new string[calls.Length];
            for (var i = 0; i < calls.Length; i++)
                sequential[i] = await ExecuteToolWithSpinnerAsync(calls[i], ctx, useSpinnerPerCall, ct).ConfigureAwait(false);
            return sequential;
        }

        var maxParallel = _options.MaxParallel;
        if (maxParallel > 0 && maxParallel < calls.Length)
        {
            // Throttled fan-out — semaphore caps concurrent in-flight executions. Useful when
            // the parent dispatches a fan of Task subagents and the LiteLLM proxy is rate-limited.
            await status.WriteLineAsync(
                Palette.Mute($"  ↳ throttled to {maxParallel} concurrent (--max-parallel)"))
                .ConfigureAwait(false);

            using var sem = new SemaphoreSlim(maxParallel);
            var throttled = new Task<string>[calls.Length];
            for (var i = 0; i < calls.Length; i++)
            {
                var call = calls[i];
                throttled[i] = RunWithSemaphore(sem, call, ctx, ct);
            }
            return await Task.WhenAll(throttled).ConfigureAwait(false);
        }

        var tasks = new Task<string>[calls.Length];
        for (var i = 0; i < calls.Length; i++)
            tasks[i] = ExecuteToolAsync(calls[i], ctx, ct);
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<string> RunWithSemaphore(
        SemaphoreSlim sem, ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try { return await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false); }
        finally { sem.Release(); }
    }

    private async Task<string> ExecuteToolWithSpinnerAsync(
        ToolCall call,
        ToolContext ctx,
        bool useSpinner,
        CancellationToken ct)
    {
        // Interactive tools (AskUserQuestion) render their own prompt and read keystrokes — a
        // Status spinner around them would seize the console and throw a nested-interactive
        // error. Run them bare so the prompt owns the terminal.
        var toolForCall = _tools.Get(call.FunctionName);
        if (toolForCall?.IsInteractive == true)
            return await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);

        if (!useSpinner || _richConsole is null)
            return await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);

        var label = $"[{Hex(BrandCyan)}]{Markup.Escape(call.FunctionName)}[/] " +
                    $"[{Hex(MuteText)}]{Markup.Escape(Truncate(call.Arguments, 80))}[/]";
        string result = string.Empty;
        await _richConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(BrandCyan))
            .StartAsync(label, async statusCtx =>
            {
                // While a (possibly long) tool runs, keep refreshing the label with the
                // type-ahead readout so the user can still see what they're typing / how many
                // messages they've queued. Only spun up when there's a type-ahead source.
                using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ticker = _typeAhead is null
                    ? Task.CompletedTask
                    : Task.Run(async () =>
                    {
                        try
                        {
                            while (!tickerCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(120, tickerCts.Token).ConfigureAwait(false);
                                statusCtx.Status(label + TypeAheadSuffix());
                            }
                        }
                        catch (OperationCanceledException) { /* normal */ }
                    }, tickerCts.Token);
                try
                {
                    result = await ExecuteToolAsync(call, ctx, ct).ConfigureAwait(false);
                }
                finally
                {
                    tickerCts.Cancel();
                    try { await ticker.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); }
                    catch { /* swallow */ }
                }
            })
            .ConfigureAwait(false);
        // Print a confirmation line after the spinner clears so the call stays in scrollback.
        _richConsole.MarkupLine($"[{Hex(BrandCyan)}]✓[/] {label}");
        return result;
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        await SafeNotifyAsync(_observer?.OnToolCallAsync(call.FunctionName, call.Arguments, ct)).ConfigureAwait(false);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var argsHash = HashArgs(call.Arguments);

        // Pre-execute exact-repeat short-circuit. Fires only when the same (tool, args)
        // appeared at least ExactRepeatThreshold times in the window AND every prior
        // occurrence returned the same result hash — i.e. results that legitimately differ
        // (Read-after-Write, Bash with side effects, WebFetch on a moving page) are NOT
        // short-circuited, the model is allowed to retry. Running cost: zero, the tool
        // doesn't execute.
        var exactBreak = CheckExactRepeat(call.FunctionName, argsHash);
        if (exactBreak is not null)
        {
            sw.Stop();
            Interlocked.Increment(ref _turnToolErrorCount);
            var msg = Volatile.Read(ref _consecutiveLoopBreaks) >= MaxConsecutiveBreaks
                ? exactBreak + "\n\n" + BuildFinalLoopExitMessage(call.FunctionName)
                : exactBreak;

            // Record the SHORT-CIRCUITED call too, with a sentinel result hash. Without
            // this, the model could oscillate between two patterns A and B (each blocked
            // once, then evicted, then blocked again) and never trip the threshold.
            // Keeping every attempt in the buffer means the threshold counts attempts,
            // not executions.
            EnqueueTrace(new ToolCallTrace(call.FunctionName, argsHash, ShortCircuitedResultMarker));

            await SafeNotifyAsync(_observer?.OnToolResultAsync(call.FunctionName, msg, true, sw.Elapsed, ct))
                .ConfigureAwait(false);
            return msg;
        }

        var (content, isError) = await ExecuteToolCoreAsync(call, ctx, ct).ConfigureAwait(false);
        sw.Stop();
        if (isError) Interlocked.Increment(ref _turnToolErrorCount);

        var resultHash = HashResult(content);
        EnqueueTrace(new ToolCallTrace(call.FunctionName, argsHash, resultHash));

        // Post-execute permutation-loop check. Scoped to search tools (Grep/Glob/WebSearch)
        // where "different args, same result" is genuinely diagnostic — for Read/Bash/etc.
        // it's a normal pattern (reading multiple stub files, running `git status` twice).
        // We've already paid for the call; appending the warning is the best we can do.
        var permutationBreak = CheckPermutationLoop(call.FunctionName, resultHash);
        var finalContent = content;
        var finalIsError = isError;
        if (permutationBreak is not null)
        {
            Interlocked.Increment(ref _turnToolErrorCount);
            finalContent = content + "\n\n" + permutationBreak;
            if (Volatile.Read(ref _consecutiveLoopBreaks) >= MaxConsecutiveBreaks)
                finalContent += "\n\n" + BuildFinalLoopExitMessage(call.FunctionName);
            finalIsError = true;
        }
        else
        {
            // Productive call (different result than recent same-tool calls) — reset the
            // consecutive-break counter. A single bad streak shouldn't doom the rest of
            // the run if the model finally finds something useful.
            Volatile.Write(ref _consecutiveLoopBreaks, 0);
        }

        await SafeNotifyAsync(_observer?.OnToolResultAsync(call.FunctionName, finalContent, finalIsError, sw.Elapsed, ct))
            .ConfigureAwait(false);
        return finalContent;
    }

    /// <summary>
    /// Pull every message the user queued during this round and combine them into one string
    /// (blank line separated), or null when nothing was queued / there's no queue at all. Called
    /// at each tool-round boundary so queued follow-ups reach the model mid-turn.
    /// </summary>
    private string? DrainQueuedInput()
    {
        if (_inputQueue is null || !_inputQueue.HasPending) return null;
        var parts = new List<string>();
        while (_inputQueue.TryDequeue(out var msg)) parts.Add(msg);
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private void EnqueueTrace(ToolCallTrace trace)
    {
        lock (_recentToolCallsLock)
        {
            _recentToolCalls.Enqueue(trace);
            while (_recentToolCalls.Count > LoopWindowSize) _recentToolCalls.Dequeue();
        }
    }

    /// <summary>
    /// Pre-execute check: count trailing-consecutive entries in the buffer whose
    /// (tool, args, result) match each other. If at least
    /// <see cref="ExactRepeatThreshold"/> - 1 trailing entries share the same result hash
    /// for these (tool, args), block the next call (it would be the Nth identical one).
    /// Earlier entries in the buffer with different result hashes are deliberately
    /// ignored — they represent genuine state transitions (Read-after-Write, Edit-then-
    /// verify) and the model is allowed to continue retrying once the state has changed.
    /// </summary>
    private string? CheckExactRepeat(string tool, string argsHash)
    {
        List<ToolCallTrace> matches;
        lock (_recentToolCallsLock)
        {
            matches = _recentToolCalls.Where(c => c.Tool == tool && c.ArgsHash == argsHash).ToList();
        }
        if (matches.Count == 0) return null;

        // Hammering past a previous break: if the most recent match for (tool, args)
        // was already a short-circuited call, the model is re-emitting the same call
        // immediately after being told to stop. Block again unconditionally — the
        // trailing-same scan below would let it through (a single loop-break sentinel
        // doesn't satisfy ExactRepeatThreshold-1).
        if (matches[^1].ResultHash == ShortCircuitedResultMarker)
        {
            Interlocked.Increment(ref _consecutiveLoopBreaks);
            return BuildLoopBreakMessage(tool, "identical_args_same_result", matches.Count + 1);
        }

        // Otherwise: walk matches from the most recent backwards, counting how many
        // consecutive entries share the same result hash. The first entry whose hash
        // differs breaks the run — that's a "state changed" point and we trust the
        // model to be allowed to act on the new state.
        var trailingHash = matches[^1].ResultHash;
        var trailingSame = 0;
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (matches[i].ResultHash != trailingHash) break;
            trailingSame++;
        }
        if (trailingSame < ExactRepeatThreshold - 1) return null;

        Interlocked.Increment(ref _consecutiveLoopBreaks);
        return BuildLoopBreakMessage(tool, "identical_args_same_result", trailingSame + 1);
    }

    /// <summary>
    /// Post-execute check: have we seen this (tool, resultHash) combination
    /// <see cref="SameResultThreshold"/>+ times in the window? Used to detect the
    /// "Grep 5 different patterns, all return (no matches)" pattern. Scoped to
    /// <see cref="SearchToolsForPermutationCheck"/>; everything else returns null.
    /// </summary>
    private string? CheckPermutationLoop(string tool, string resultHash)
    {
        if (!SearchToolsForPermutationCheck.Contains(tool)) return null;

        int sameToolSameResult;
        lock (_recentToolCallsLock)
        {
            sameToolSameResult = _recentToolCalls.Count(c => c.Tool == tool && c.ResultHash == resultHash);
        }
        if (sameToolSameResult < SameResultThreshold) return null;

        Interlocked.Increment(ref _consecutiveLoopBreaks);
        return BuildLoopBreakMessage(tool, "permuted_args_same_result", sameToolSameResult);
    }

    /// <summary>
    /// Stable 16-hex-char fingerprint of a tool's argument JSON. Keys are sorted before
    /// hashing so {"a":1,"b":2} fingerprints identically to {"b":2,"a":1} — open-source
    /// models rotate key order between native and XML mode, and an earlier prototype
    /// missed half the loop cases without normalisation. Garbage in (un-parseable JSON)
    /// → fingerprint of the literal bytes; still works as a stable identifier.
    /// </summary>
    private static string HashArgs(string argsJson)
    {
        string normalized;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            normalized = JsonNormaliser.SortKeys(doc.RootElement);
        }
        catch (JsonException)
        {
            normalized = argsJson;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>
    /// Hash of a tool result. We hash the first 128 + last 128 chars (joined with a
    /// separator) and collapse whitespace — defeats trivial cosmetic diffs while
    /// catching cases where two outputs share a long prefix (filename + headers) but
    /// differ in the tail (the actual data). Truncating to a head slice alone, as the
    /// initial prototype did, would treat distinct outputs with the same metadata
    /// header as identical.
    /// </summary>
    private static string HashResult(string content)
    {
        const int sliceLen = 128;
        ReadOnlySpan<char> span = content;
        string joined;
        if (span.Length <= sliceLen * 2)
        {
            joined = content;
        }
        else
        {
            var head = span[..sliceLen].ToString();
            var tail = span[^sliceLen..].ToString();
            joined = head + "|" + tail;
        }
        var collapsed = WhitespaceRun.Replace(joined, " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(collapsed)))[..16];
    }

    private static string BuildLoopBreakMessage(string tool, string kind, int count) => kind switch
    {
        "identical_args_same_result" =>
            $"[loop-break] You called `{tool}` with identical arguments {count} times in the last " +
            $"{LoopWindowSize} tool calls and the result was identical every time. " +
            "Stop repeating — change tools, change arguments meaningfully, or accept the current " +
            "state and proceed with what you already know.",
        "permuted_args_same_result" =>
            $"[loop-break] You called `{tool}` {count} times in the last {LoopWindowSize} tool calls " +
            "with different arguments but got the same result every time. This usually means " +
            "the codebase doesn't contain what you're searching for. Stop permuting the arguments " +
            "— accept the absence of matches and proceed with the analysis you can perform " +
            "based on what you've already read.",
        _ => $"[loop-break] {tool}: detected unproductive loop pattern; change strategy.",
    };

    private static string BuildFinalLoopExitMessage(string tool) =>
        $"[loop-break-final] You have ignored {MaxConsecutiveBreaks} consecutive loop-break warnings. " +
        "Stop calling tools and write your final response based on the information you've already " +
        "gathered. Do not call any more tools this turn.";

    /// <summary>
    /// Recursive JSON normaliser: sorts object keys lexicographically, preserves array
    /// order (arrays are positional), and writes primitives unchanged. Used by
    /// <see cref="HashArgs"/> so semantically-equivalent argument blobs collide on the
    /// same fingerprint regardless of key ordering choices the model makes.
    /// </summary>
    private static class JsonNormaliser
    {
        public static string SortKeys(JsonElement root)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                Write(writer, root);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void Write(Utf8JsonWriter w, JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        w.WritePropertyName(p.Name);
                        Write(w, p.Value);
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in el.EnumerateArray()) Write(w, item);
                    w.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    w.WriteStringValue(el.GetString());
                    break;
                case JsonValueKind.Number:
                    // RawText preserves the exact textual form (integer vs decimal).
                    w.WriteRawValue(el.GetRawText(), skipInputValidation: true);
                    break;
                case JsonValueKind.True:
                    w.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    w.WriteBooleanValue(false);
                    break;
                case JsonValueKind.Null:
                    w.WriteNullValue();
                    break;
                default:
                    w.WriteNullValue();
                    break;
            }
        }
    }

    private async Task<(string Content, bool IsError)> ExecuteToolCoreAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var tool = _tools.Get(call.FunctionName);
        if (tool is null)
            return (BuildUnknownToolMessage(call.FunctionName), true);

        // Plan-mode hard guard: refuse the mutating tools regardless of permission rules. The
        // model is told (via the reminder + this message) to draft a plan and call ExitPlanMode.
        if (_planMode?.InPlanMode == true && PlanModeState.BlockedTools.Contains(call.FunctionName))
            return ($"[blocked: plan mode is ON] `{call.FunctionName}` modifies the workspace and is " +
                    "not allowed while planning. Finish investigating with read-only tools, then call " +
                    "ExitPlanMode with your plan to get the user's approval before making changes.", true);

        JsonDocument argsDoc;
        try
        {
            argsDoc = string.IsNullOrWhiteSpace(call.Arguments)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(call.Arguments);
        }
        catch (JsonException ex)
        {
            return ($"[Invalid arguments JSON for {call.FunctionName}: {ex.Message}]", true);
        }

        using (argsDoc)
        {
            var args = argsDoc.RootElement;
            var specifier = tool.GetSpecifierForPermissions(args);
            var decision = _perms.Evaluate(call.FunctionName, specifier);

            if (decision == PermissionDecision.Deny)
                return ($"[Permission denied: {call.FunctionName}({specifier ?? string.Empty})]", true);

            if (decision == PermissionDecision.Ask && !_options.SkipPermissions)
                return ($"[Permission required for {call.FunctionName}({specifier ?? string.Empty}). " +
                       "Add an allow rule in settings.json or pass --dangerously-skip-permissions.]", true);

            var res = await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false);
            return (res.Content, res.IsError);
        }
    }

    /// <summary>
    /// Build the body returned to the model when it calls a tool that isn't in the registry.
    /// The historical "[Unknown tool: X]" was a single line and weak models (especially in
    /// XML mode) silently gave up on it; emitting the available alternatives + the reason
    /// the tool might be missing lets the model report something useful to the operator
    /// instead of fabricating a plausible-looking but empty completion. Capped at 32 names
    /// so a registry of dozens (Read/Glob/Grep + many MCP tools) doesn't blow the per-message
    /// budget that some chat templates enforce.
    /// </summary>
    private string BuildUnknownToolMessage(string requested)
    {
        var available = _tools.Schemas.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        const int cap = 32;
        var shown = available.Count <= cap
            ? string.Join(", ", available)
            : string.Join(", ", available.Take(cap)) + $", … (+{available.Count - cap} more)";
        return $"[Unknown tool: {requested}]\n" +
               $"Available tools: {shown}.\n" +
               "Hint: this tool may have been filtered by --allowed-tools, " +
               "or its MCP server failed to start. Do not retry the same name; " +
               "use one of the available tools or report the missing tool to the operator.";
    }

    /// <summary>
    /// Best-effort observer notification: awaits the supplied task but swallows any exception
    /// (and respects null) so a misbehaving observer can't crash the agent. Cancellations
    /// propagate normally — they're a legitimate caller signal, not an observer bug.
    /// </summary>
    private static async Task SafeNotifyAsync(Task? notification)
    {
        if (notification is null) return;
        try { await notification.ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* swallow — observers are decorative */ }
    }

    private static string FormatStatusLine(string toolName, string arguments) =>
        $"{Palette.Cyan($"[{toolName}]")} {Palette.Mute(Truncate(arguments, 80))}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Build the live spinner label: <c>thinking (3m 39s · ↓ 11.3k tokens)</c>. Tokens are
    /// approximated as chars/4, the conventional rule-of-thumb that's fine for English/code;
    /// servers don't send incremental usage so this is the best signal we can show before the
    /// final Usage chunk arrives. We still display the *real* prompt/completion totals after
    /// the turn ends via the existing /context output and the observer's OnFinal.
    /// </summary>
    private string BuildSpinnerLabel(TimeSpan elapsed, int charsStreamed)
    {
        var seconds = (int)elapsed.TotalSeconds;
        string time;
        if (seconds < 60) time = $"{seconds}s";
        else if (seconds < 3600) time = $"{seconds / 60}m {seconds % 60}s";
        else time = $"{seconds / 3600}h {(seconds % 3600) / 60}m";

        var approxTokens = charsStreamed / 4;
        string tokens;
        if (approxTokens < 1_000) tokens = approxTokens.ToString();
        else if (approxTokens < 1_000_000) tokens = $"{approxTokens / 1000.0:F1}k";
        else tokens = $"{approxTokens / 1_000_000.0:F1}M";

        return $"[{Hex(BrandCyan)}]thinking[/] " +
               $"[{Hex(MuteText)}]({time} · ↓ {tokens} tokens)[/]" +
               TypeAheadSuffix();
    }

    /// <summary>
    /// Builds the trailing bit of the spinner label that shows mid-turn typing / queued messages,
    /// so the user sees their input is being taken instead of thinking the terminal froze. Empty
    /// when nothing is being typed or queued (or when there's no type-ahead source at all).
    /// </summary>
    private string TypeAheadSuffix()
    {
        if (_typeAhead is null) return string.Empty;
        var typing = _typeAhead.CurrentInput;
        var queued = _typeAhead.QueuedCount;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(typing))
        {
            var shown = typing.Length > 48 ? "…" + typing[^48..] : typing;
            sb.Append($"  [{Hex(BrandGold)}]⌨ {Markup.Escape(shown)}▏[/] ")
              .Append($"[{Hex(MuteText)}](Enter to queue)[/]");
        }
        if (queued > 0)
        {
            sb.Append($"  [{Hex(BrandGold)}]{queued} queued[/]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Throttle spinner-label redraws to ~10 per second. Without throttling, a fast model
    /// streaming 50+ chunks/sec would Status() faster than Spectre can repaint and we'd see
    /// flicker / wasted work. The throttle is per-turn (lastUpdate is reset by the caller).
    /// </summary>
    private void UpdateSpinnerThrottled(
        StatusContext? statusCtx,
        System.Diagnostics.Stopwatch sw,
        int charsStreamed,
        ref TimeSpan lastUpdate)
    {
        if (statusCtx is null) return;
        var now = sw.Elapsed;
        if (now - lastUpdate < TimeSpan.FromMilliseconds(100)) return;
        lastUpdate = now;
        statusCtx.Status(BuildSpinnerLabel(now, charsStreamed));
    }

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

    /// <summary>
    /// If <paramref name="call"/>'s arguments parse as a single JSON value, returns a
    /// 1-element list with the original — the common case, bit-for-bit unchanged. If the
    /// args fail to parse AND can be sliced into 2+ independent valid JSON objects (the
    /// GLM-5 parallel-tool-calls bug: <c>{"x":1}{"y":2}{"z":3}</c>), returns N ToolCalls
    /// with the same function name and one slice each. Any other failure mode — truly
    /// malformed args, single object that doesn't parse, mixed-validity slices — returns
    /// the original untouched, so the downstream tool surfaces its own clear error.
    /// </summary>
    internal static IReadOnlyList<ToolCall> SplitConcatenatedArgs(ToolCall call, TextWriter? status = null)
    {
        if (string.IsNullOrWhiteSpace(call.Arguments))
            return [call];

        // Quick path: a single valid JSON value parses cleanly. Covers every well-behaved
        // model + the edge case where args legitimately contain `}{` inside a JSON string.
        if (IsValidJson(call.Arguments))
            return [call];

        // Try slicing into top-level {...} blocks. We commit to a split only if (a) we get
        // ≥2 slices and (b) every slice independently parses as JSON — otherwise we can't
        // trust the boundaries we drew, and pass through is safer than synthetic sub-calls.
        var parts = SliceTopLevelObjects(call.Arguments);
        if (parts.Count <= 1)
            return [call];

        foreach (var p in parts)
            if (!IsValidJson(p))
                return [call];

        status?.WriteLine(
            $"  ↳ {call.FunctionName}: detected {parts.Count} concatenated arg objects, splitting into separate calls");

        var split = new ToolCall[parts.Count];
        for (var i = 0; i < parts.Count; i++)
            split[i] = new ToolCall($"{call.Id}_s{i}", call.FunctionName, parts[i]);
        return split;
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static List<string> SliceTopLevelObjects(string args)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    parts.Add(args.Substring(start, i - start + 1));
                    start = -1;
                }
            }
        }
        return parts;
    }
}
