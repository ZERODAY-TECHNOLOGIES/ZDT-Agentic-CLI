using Spectre.Console;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Workflows;
using Zdtllm.Tools;

namespace Zdtllm.Core.Repl;

/// <summary>
/// Interactive REPL loop. Reads single-line input from the given TextReader,
/// dispatches slash commands or runs a session turn, repeats until /exit or EOF.
/// All I/O goes through the supplied readers/writers so this class is unit-testable
/// without touching System.Console.
/// </summary>
public sealed class Repl
{
    private static readonly Spectre.Console.Color BrandCyan = new(0x1B, 0xEA, 0xCD);
    private static readonly Spectre.Console.Color BrandGold = new(0xE5, 0xD9, 0x36);
    private static readonly Spectre.Console.Color MuteText = new(0x68, 0x7B, 0x89);
    private static readonly Spectre.Console.Color BorderTint = new(0x36, 0x4A, 0x5E);
    private static readonly Spectre.Console.Color RedAccent = new(0xE5, 0x4D, 0x4D);

    private readonly Session _session;
    private readonly AgentLoop _agent;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _cwd;
    private readonly ReplOptions _options;
    private readonly IAnsiConsole? _richConsole;
    private readonly ISubagentRunner? _subagentRunner;
    private readonly IUserInputQueue? _inputQueue;
    private readonly ITurnInputCapture? _inputCapture;
    private readonly IPlanModeSwitch? _planMode;
    /// <summary>Renders the current MCP server status for <c>/mcp</c>. Supplied by the CLI (which
    /// owns the McpManager) so Core needn't depend on Zdtllm.Mcp. Null when MCP is unavailable.</summary>
    private readonly Func<string>? _mcpStatus;
    private IReplInputSource? _richInput;
    private CancellationTokenSource? _currentTurnCts;

    public Repl(
        Session session,
        AgentLoop agent,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string cwd,
        ReplOptions? options = null,
        IAnsiConsole? richConsole = null,
        ISubagentRunner? subagentRunner = null,
        IUserInputQueue? inputQueue = null,
        ITurnInputCapture? inputCapture = null,
        IPlanModeSwitch? planMode = null,
        IReplInputSource? richInput = null,
        Func<string>? mcpStatus = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrEmpty(cwd);

        _session = session;
        _agent = agent;
        _input = input;
        _output = output;
        _error = error;
        _cwd = cwd;
        _options = options ?? new ReplOptions();
        _richConsole = richConsole;
        _subagentRunner = subagentRunner;
        _inputQueue = inputQueue;
        _inputCapture = inputCapture;
        _planMode = planMode;
        _mcpStatus = mcpStatus;
        _richInput = richInput;
    }

    /// <summary>
    /// Cancels whatever turn is currently in flight, if any. Wired up to
    /// Console.CancelKeyPress in Program.cs so Ctrl+C halts the running agent
    /// (and any subagents it spawned, since they share this token chain) but
    /// keeps the REPL alive for the next prompt.
    /// </summary>
    /// <summary>
    /// True while a turn is executing (used by the Ctrl+C handler to decide between
    /// "interrupt the running turn" and "arm the press-again-to-exit hint").
    /// </summary>
    public bool IsTurnActive => _currentTurnCts is not null;

    public void CancelCurrentTurn()
    {
        // The CTS is owned by the per-turn `using` in ProcessUserTurnAsync. CancelKeyPress
        // can fire AFTER the using has disposed but BEFORE _currentTurnCts is cleared on
        // a different thread — racing into a disposed CTS would throw ObjectDisposedException.
        // Swallowing is fine: there's nothing to cancel anymore.
        try { _currentTurnCts?.Cancel(); }
        catch (ObjectDisposedException) { /* race with turn-end disposal — nothing to do */ }
    }

    public async Task<int> RunAsync(string? initialPrompt = null, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(initialPrompt))
            {
                await RunTurnAndFollowupsAsync(initialPrompt, ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await ReadInputLineAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return 0; }

                if (line is null) // EOF (Ctrl+D) / exit request
                {
                    await _output.WriteLineAsync().ConfigureAwait(false);
                    return 0;
                }

                var trimmed = line.Trim();

                if (trimmed.StartsWith('/'))
                {
                    _richInput?.TakePendingImages(); // a slash command carries no attachment
                    var slashResult = await HandleSlashAsync(trimmed, ct).ConfigureAwait(false);
                    if (slashResult == SlashOutcome.Exit) return 0;
                    continue;
                }

                // Images the user dragged onto the prompt while composing this line (vision models).
                var images = _richInput?.TakePendingImages();
                var hasImages = images is { Count: > 0 };

                // Blank line and nothing attached → nothing to do. A blank line WITH an image is a
                // valid "look at this" turn; give the model a default prompt so it has something.
                if (trimmed.Length == 0 && !hasImages) continue;
                if (trimmed.Length == 0) trimmed = "(see the attached image)";

                await RunTurnAndFollowupsAsync(trimmed, ct, images).ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            // Print the closed-session farewell on EVERY exit path — /exit, EOF, or Ctrl+C
            // (which cancels ct and drops us out of the loop) — exactly like claude-cli.
            PrintFarewell();
        }
    }

    /// <summary>
    /// On the way out, tell the user which session just closed and how to pick it back up —
    /// so a resume is one paste away. Ephemeral sessions have nothing to resume; say so.
    /// Idempotent: safe to call from both the RunAsync finally and the Ctrl+C exit handler
    /// (a double Ctrl+C hard-exits before the finally can run reliably), it prints only once.
    /// Synchronous so it completes even on the abrupt Ctrl+C teardown path.
    /// </summary>
    public void PrintFarewell()
    {
        if (Interlocked.Exchange(ref _farewellShown, 1) == 1) return;
        _output.WriteLine();
        if (_session.IsPersistent)
        {
            _output.WriteLine(
                Palette.Mute("Session ") + Palette.Body(_session.Id) + Palette.Mute(" closed. Resume it with  ") +
                Palette.Cyan($"zdt -r {_session.Id}"));
            _output.WriteLine(
                Palette.Mute("or pick from recent conversations with  ") + Palette.Cyan("zdt -r"));
        }
        else
        {
            _output.WriteLine(Palette.Mute("Session ended (ephemeral — nothing was saved)."));
        }
        _output.Flush();
    }

    private int _farewellShown;

    /// <summary>
    /// Read one line of input. Uses the rich line editor (multi-line paste, drag & drop) when one
    /// is wired in; if it ever throws, that path is disabled for the rest of the session and we
    /// fall back to the classic prompt + <c>ReadLine</c> — so a terminal quirk can never brick
    /// input. The classic path prints its own "> " prompt; the rich editor draws its own.
    /// </summary>
    private async Task<string?> ReadInputLineAsync(CancellationToken ct)
    {
        if (_richInput is not null)
        {
            try
            {
                return await _richInput.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _richInput = null; // fall back permanently for this session
                await _error.WriteLineAsync(
                    Palette.Mute($"(rich input disabled: {ex.Message}; using basic line input)"))
                    .ConfigureAwait(false);
            }
        }

        await _output.WriteAsync(Palette.Cyan("> ")).ConfigureAwait(false);
        await _output.FlushAsync(ct).ConfigureAwait(false);
        return await _input.ReadLineAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="prompt"/> as a turn, then drains any messages the user queued while
    /// it ran (interactive queueing) and runs each as a follow-up turn — so typing while the
    /// model works never gets lost, whether the AgentLoop folded it in mid-turn or it arrived
    /// after the last tool round. With no queue configured (tests, non-interactive) this is a
    /// single ProcessUserTurnAsync call, unchanged.
    /// </summary>
    private async Task RunTurnAndFollowupsAsync(
        string prompt, CancellationToken ct, IReadOnlyList<string>? images = null)
    {
        string? next = prompt;
        // Images attach only to the first turn — the one the user typed with the drop. Queued
        // follow-ups are text-only.
        var turnImages = images;
        while (next is not null && !ct.IsCancellationRequested)
        {
            await ProcessUserTurnAsync(next, ct, turnImages).ConfigureAwait(false);
            turnImages = null;

            if (_inputQueue is not null && _inputQueue.TryDequeue(out var queued))
            {
                await _output.WriteLineAsync(
                    Palette.Cyan("▶ running queued message: ") + Palette.Mute(Truncate(queued, 80)))
                    .ConfigureAwait(false);
                next = queued;
            }
            else
            {
                next = null;
            }
        }
    }

    private static string Truncate(string s, int max)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
    }

    private async Task ProcessUserTurnAsync(
        string prompt, CancellationToken ct, IReadOnlyList<string>? images = null)
    {
        // Mid-turn auto-compact (hard threshold) is now AgentLoop's responsibility — it
        // fires between iterations regardless of whether we're driving the parent here
        // or a subagent inside SubagentRunner. We keep ONLY the post-turn soft-threshold
        // hint here because it's a UI nudge specific to the interactive REPL.
        //
        // Per-turn CTS is linked to the outer ct so program-level shutdown cancels
        // everything, but Ctrl+C only kills THIS turn — the next prompt iteration gets
        // a fresh token. The current turn's cts is published via _currentTurnCts so
        // the Cli's CancelKeyPress hook can flip it without holding a direct reference
        // to AgentLoop / its tool plumbing.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _currentTurnCts = turnCts;
        // Start capturing keystrokes into the queue so the user can type follow-ups while the
        // model works. No-op when no capture driver is wired (tests / non-interactive).
        _inputCapture?.BeginCapture();
        try
        {
            await _agent.RunTurnAsync(_session, prompt, _output, _error, turnCts.Token, images).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
        {
            // Our CT chain is what fired — i.e. user pressed Ctrl+C and CancelCurrentTurn ran,
            // or the program-level CT was cancelled. This is the genuine "user cancelled" path.
            await _error.WriteLineAsync(Palette.Mute("(turn cancelled)")).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // OCE that ISN'T our CT — almost always an HttpClient.Timeout firing on the LLM
            // call. Surface this as a real error rather than masquerading as user cancellation,
            // so the user can spot misconfigured timeouts instead of staring at a confusing
            // "(turn cancelled)" they didn't trigger.
            await _error.WriteLineAsync(
                Palette.Red("zdt: turn aborted — request timed out (HttpClient.Timeout). ") +
                Palette.Mute("Set litellm.timeoutSeconds in settings.json (or remove it for no timeout). " +
                            $"[{ex.GetType().Name}: {ex.Message}]"))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync(Palette.Red($"zdt: {ex.Message}")).ConfigureAwait(false);
        }
        finally
        {
            // Stop the capture reader before we return so the next idle prompt has sole ownership
            // of stdin (the capture reader and the idle ReadLine must never be active together).
            if (_inputCapture is not null)
                await _inputCapture.EndCaptureAsync().ConfigureAwait(false);
            _currentTurnCts = null;
        }

        var ctx = _agent.Context;
        if (ctx is not null && ctx.IsBeyondSoftThreshold && !ctx.IsBeyondHardThreshold)
        {
            await _error.WriteLineAsync(
                Palette.Gold($"[context at {ctx.UsagePercent}%]") + " " +
                Palette.Mute("/compact recommended"))
                .ConfigureAwait(false);
        }
    }

    private async Task<SlashOutcome> HandleSlashAsync(string line, CancellationToken ct)
    {
        var (cmd, args) = SplitCommand(line);

        switch (cmd)
        {
            case "/exit":
            case "/quit":
                return SlashOutcome.Exit;

            case "/help":
                await PrintHelpAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/clear":
                _session.ClearKeepingSystem();
                await _output.WriteLineAsync(
                    Palette.Cyan("✓") + " " + Palette.Mute("Conversation history cleared (system prompt kept)."))
                    .ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/status":
                await PrintStatusAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/init":
                await InitMemoryFileAsync(ct).ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/model":
                await HandleModelCommandAsync(args).ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/tool-calling":
                await HandleToolCallingCommandAsync(args).ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/permissions":
                await PrintPermissionsAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/mcp":
                await PrintMcpStatusAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/agents":
                await PrintAgentsAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/compact":
                await HandleCompactCommandAsync(ct).ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/context":
                await HandleContextCommandAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/plan":
                await HandlePlanCommandAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/mode":
                await HandleModeCommandAsync(args).ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/workflows":
                await ListWorkflowsAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/workflow":
                await HandleWorkflowCommandAsync(args, ct).ConfigureAwait(false);
                return SlashOutcome.Continue;

            default:
                await _output.WriteLineAsync(
                        Palette.Red($"Unknown command: {cmd}.") + " " +
                        Palette.Mute("Type ") + Palette.Body("/help") + Palette.Mute(" for the available commands."))
                    .ConfigureAwait(false);
                return SlashOutcome.Continue;
        }
    }

    private static (string Cmd, string Args) SplitCommand(string line)
    {
        var space = line.IndexOf(' ');
        if (space < 0) return (line.ToLowerInvariant(), string.Empty);
        return (line[..space].ToLowerInvariant(), line[(space + 1)..].Trim());
    }

    private async Task PrintHelpAsync()
    {
        await _output.WriteLineAsync(Palette.BodyBold("Available commands:")).ConfigureAwait(false);
        await WriteCommandRowAsync("/help", "show this list").ConfigureAwait(false);
        await WriteCommandRowAsync("/exit, /quit", "leave the REPL").ConfigureAwait(false);
        await WriteCommandRowAsync("/clear", "drop conversation history (system prompt kept)").ConfigureAwait(false);
        await WriteCommandRowAsync("/status", "show session id, model, mode, message count").ConfigureAwait(false);
        await WriteCommandRowAsync("/context", "show current context-window usage and per-role breakdown").ConfigureAwait(false);
        await WriteCommandRowAsync("/model <name>", "switch model used by the next turn").ConfigureAwait(false);
        await WriteCommandRowAsync("/tool-calling <native|xml>", "switch tool-call transport for the next turn").ConfigureAwait(false);
        await WriteCommandRowAsync("/permissions", "show the current permission rule set").ConfigureAwait(false);
        await WriteCommandRowAsync("/init", "create ZDTLLM.md (project memory file) in the cwd").ConfigureAwait(false);
        await WriteCommandRowAsync("/compact", "summarize older turns to free context").ConfigureAwait(false);
        await WriteCommandRowAsync("/plan", "toggle plan mode (read-only; propose a plan before changes)").ConfigureAwait(false);
        await WriteCommandRowAsync("/workflows", "list declarative workflows in .zdtllm/workflows/").ConfigureAwait(false);
        await WriteCommandRowAsync("/workflow <name> [k=v]", "run a multi-agent workflow").ConfigureAwait(false);
        await WriteCommandRowAsync("/agents", "list available subagent types and their tool sets").ConfigureAwait(false);
    }

    private async Task PrintAgentsAsync()
    {
        if (_subagentRunner is null)
        {
            await _output.WriteLineAsync(
                Palette.Mute("/agents requires the Agent tool to be wired up. ") +
                Palette.Mute("(Re-launch zdt; subagents are configured automatically in interactive mode.)"))
                .ConfigureAwait(false);
            return;
        }

        var infos = _subagentRunner.GetTypeInfo();
        await _output.WriteLineAsync(
                Palette.BodyBold("Subagent profiles:") + " " +
                Palette.Mute($"({infos.Count} available)"))
            .ConfigureAwait(false);

        if (_richConsole is not null && infos.Count > 0)
        {
            // Spectre table version — three columns (type, blurb, allowed tools) for fast scanning.
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(BorderTint)
                .Title($"[bold {Hex(BrandCyan)}]subagent types[/]");
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(BrandCyan)}]type[/]")));
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(BrandGold)}]description[/]")));
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(MuteText)}]tools[/]")));

            foreach (var info in infos)
            {
                var tools = info.AllowedTools.Count == 1 && info.AllowedTools[0] == "*"
                    ? "all (except Task)"
                    : string.Join(", ", info.AllowedTools);
                table.AddRow(
                    new Markup($"[bold {Hex(BrandGold)}]{Markup.Escape(info.Name)}[/]"),
                    new Markup(Markup.Escape(info.Description)),
                    new Markup($"[{Hex(MuteText)}]{Markup.Escape(tools)}[/]"));
            }
            _richConsole.Write(table);
        }
        else
        {
            // Plain text fallback — same content, line-per-type.
            foreach (var info in infos)
            {
                var tools = info.AllowedTools.Count == 1 && info.AllowedTools[0] == "*"
                    ? "all (except Task)"
                    : string.Join(", ", info.AllowedTools);
                await _output.WriteLineAsync(
                        $"  {Palette.GoldBold(info.Name.PadRight(18))} {Palette.Body(info.Description)}")
                    .ConfigureAwait(false);
                await _output.WriteLineAsync(
                        $"    {Palette.Mute("tools: " + tools)}")
                    .ConfigureAwait(false);
            }
        }

        await _output.WriteLineAsync(
                Palette.Mute("  Spawned via the Agent tool. Failed runs auto-retry once and may fall back to general-purpose."))
            .ConfigureAwait(false);
    }

    private Task WriteCommandRowAsync(string command, string description) =>
        _output.WriteLineAsync($"  {Palette.Cyan(command),-26} {Palette.Mute(description)}");

    private async Task PrintStatusAsync()
    {
        await _output.WriteLineAsync($"  {Palette.Mute("session:")} {Palette.Body(SessionDisplay())}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  {Palette.Mute("model:")} {Palette.GoldBold(_session.Model)}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  {Palette.Mute("mode:")} {Palette.Body(_session.Mode.ToString().ToLowerInvariant())}").ConfigureAwait(false);
        if (_planMode?.InPlanMode == true)
            await _output.WriteLineAsync($"  {Palette.Mute("plan:")} {Palette.Gold("ON (read-only)")}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  {Palette.Mute("messages:")} {Palette.Body(_session.Messages.Count.ToString())}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  {Palette.Mute("cwd:")} {Palette.Body(_cwd)}").ConfigureAwait(false);

        var ctx = _agent.Context;
        if (ctx is not null)
        {
            await _output.WriteLineAsync(
                $"  {Palette.Mute("context:")} {Palette.Body($"{ctx.LastPromptTokens:N0} / {ctx.ContextWindow:N0}")} " +
                Palette.Mute($"tokens ({ctx.UsagePercent}%)"))
                .ConfigureAwait(false);
        }
    }

    private async Task HandleCompactCommandAsync(CancellationToken ct)
    {
        var ctx = _agent.Context;
        if (ctx is null)
        {
            await _output.WriteLineAsync(
                Palette.Mute("/compact requires a configured context window. Set litellm.contextWindows.<tier> in settings."))
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var collapsed = await ctx.CompactAsync(_session, _agent.Client, ct).ConfigureAwait(false);
            if (collapsed == 0)
            {
                await _output.WriteLineAsync(
                    Palette.Mute("Nothing to compact — fewer than 5 user turns in history."))
                    .ConfigureAwait(false);
            }
            else
            {
                await _output.WriteLineAsync(
                    Palette.Cyan("✓") + " " +
                    Palette.Body($"Compacted {collapsed} message(s) into a summary; ") +
                    Palette.Mute("the last 4 user turns are preserved."))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync(Palette.Red($"zdt: /compact failed: {ex.Message}")).ConfigureAwait(false);
        }
    }

    private async Task InitMemoryFileAsync(CancellationToken ct)
    {
        var path = Path.Combine(_cwd, "ZDTLLM.md");
        if (File.Exists(path))
        {
            await _output.WriteLineAsync(
                Palette.Mute($"ZDTLLM.md already exists at ") + Palette.Body(path) +
                Palette.Mute(" — leaving it alone."))
                .ConfigureAwait(false);
            return;
        }

        const string Template = """
            # ZDTLLM.md

            Project memory for the [zer0day.ro](https://zer0day.ro) `zdtllmcli` agent.
            Notes here are loaded into the system prompt on every session for this project.

            ## What is this project?

            <!-- Brief description of the project. -->

            ## Conventions

            <!-- Coding style, naming, anything an agent should know before editing. -->

            ## Useful commands

            <!-- Build / test / lint / deploy commands the agent can call via Bash. -->
            """;

        await File.WriteAllTextAsync(path, Template, ct).ConfigureAwait(false);
        await _output.WriteLineAsync(Palette.Cyan("✓") + " " + Palette.Body($"Created {path}"))
            .ConfigureAwait(false);
    }

    private async Task HandleModelCommandAsync(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _output.WriteLineAsync($"{Palette.Mute("Current model:")} {Palette.GoldBold(_session.Model)}")
                .ConfigureAwait(false);
            await _output.WriteLineAsync(Palette.Mute("Usage: /model <name>")).ConfigureAwait(false);
            return;
        }

        _session.SetModel(args.Trim());
        await _output.WriteLineAsync(
            Palette.Cyan("✓") + " " + Palette.Body($"Model set to {_session.Model}") +
            Palette.Mute(" (takes effect on next turn)."))
            .ConfigureAwait(false);
    }

    private async Task HandleToolCallingCommandAsync(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _output.WriteLineAsync(
                $"{Palette.Mute("Current tool-calling mode:")} {Palette.GoldBold(_session.Mode.ToString().ToLowerInvariant())}")
                .ConfigureAwait(false);
            await _output.WriteLineAsync(Palette.Mute("Usage: /tool-calling <native|xml>"))
                .ConfigureAwait(false);
            return;
        }

        if (!ToolCallingModeParse.TryParse(args.Trim(), out var newMode))
        {
            await _output.WriteLineAsync(
                Palette.Red($"Unknown mode: {args.Trim()}.") + " " +
                Palette.Mute("Use ") + Palette.Body("native") + Palette.Mute(" or ") +
                Palette.Body("xml") + Palette.Mute("."))
                .ConfigureAwait(false);
            return;
        }

        _session.SetMode(newMode);
        await _output.WriteLineAsync(
            Palette.Cyan("✓") + " " +
            Palette.Body($"Tool-calling mode set to {newMode.ToString().ToLowerInvariant()}") +
            Palette.Mute(" (takes effect on next turn)."))
            .ConfigureAwait(false);
    }

    private async Task PrintPermissionsAsync()
    {
        var rs = _agent.Permissions;
        var counts = rs.RuleCounts;

        // Header line is identical in both modes — the existing tests grep for "rules:" + tokens
        // ("deny=0", "ask=0", "allow=0") and we must not break them.
        await _output.WriteLineAsync(
                $"  {Palette.Mute("rules:")} " +
                $"{Palette.Red($"deny={counts.deny}")} " +
                $"{Palette.Gold($"ask={counts.ask}")} " +
                $"{Palette.Cyan($"allow={counts.allow}")}")
            .ConfigureAwait(false);

        if (_richConsole is not null && counts is not (0, 0, 0))
        {
            // Side-by-side table — one column per precedence bucket. Empty buckets render as "—".
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(BorderTint)
                .Title($"[bold {Hex(BrandCyan)}]permission rules[/]");
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(RedAccent)}]deny[/]")));
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(BrandGold)}]ask[/]")));
            table.AddColumn(new TableColumn(new Markup($"[bold {Hex(BrandCyan)}]allow[/]")));

            var deny = rs.DenyRules;
            var ask = rs.AskRules;
            var allow = rs.AllowRules;
            var rowCount = Math.Max(deny.Count, Math.Max(ask.Count, allow.Count));

            for (var i = 0; i < rowCount; i++)
            {
                var d = i < deny.Count ? Markup.Escape(deny[i]) : $"[{Hex(MuteText)}]—[/]";
                var a = i < ask.Count ? Markup.Escape(ask[i]) : $"[{Hex(MuteText)}]—[/]";
                var w = i < allow.Count ? Markup.Escape(allow[i]) : $"[{Hex(MuteText)}]—[/]";
                table.AddRow(new Markup(d), new Markup(a), new Markup(w));
            }
            _richConsole.Write(table);
        }

        await _output.WriteLineAsync(
                Palette.Mute("  Defaults: tools requiring permission (Bash, Edit, Write, WebFetch, WebSearch, Skill) Ask without an explicit allow."))
            .ConfigureAwait(false);
    }

    private async Task PrintMcpStatusAsync()
    {
        if (_mcpStatus is null)
        {
            await _output.WriteLineAsync(Palette.Mute(
                "  No MCP servers configured. Pass --mcp-config <file> to connect one."))
                .ConfigureAwait(false);
            return;
        }

        var text = _mcpStatus();
        await _output.WriteLineAsync(string.IsNullOrWhiteSpace(text)
            ? Palette.Mute("  No MCP servers configured.")
            : text).ConfigureAwait(false);
    }

    private static string Hex(Spectre.Console.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private async Task HandlePlanCommandAsync()
    {
        if (_planMode is null)
        {
            await _output.WriteLineAsync(
                Palette.Mute("/plan needs interactive mode (it's not available in -p / non-TTY runs)."))
                .ConfigureAwait(false);
            return;
        }

        if (_planMode.InPlanMode)
        {
            _planMode.Approve();
            await _output.WriteLineAsync(
                Palette.Cyan("✓") + " " + Palette.Body("Plan mode OFF") + " " +
                Palette.Mute("— edits and commands are allowed again."))
                .ConfigureAwait(false);
        }
        else
        {
            _planMode.Enter();
            await _output.WriteLineAsync(
                Palette.Gold("◆ Plan mode ON") + " " +
                Palette.Mute("— read-only. The agent will research and propose a plan; " +
                             "approve it (or run /plan again) to make changes."))
                .ConfigureAwait(false);
        }
    }

    private async Task HandleModeCommandAsync(string args)
    {
        if (_planMode is not IPermissionModeSwitch pm)
        {
            await _output.WriteLineAsync(
                Palette.Mute("/mode needs interactive mode (not available in -p / non-TTY runs)."))
                .ConfigureAwait(false);
            return;
        }

        var arg = args.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        PermissionMode now;
        if (arg.Length == 0)
        {
            now = pm.Cycle();
        }
        else
        {
            PermissionMode? target = arg switch
            {
                "default" or "ask" => PermissionMode.Default,
                "acceptedits" or "edits" => PermissionMode.AcceptEdits,
                "plan" => PermissionMode.Plan,
                "bypass" or "bypasspermissions" or "yolo" => PermissionMode.Bypass,
                _ => null,
            };
            if (target is null)
            {
                await _output.WriteLineAsync(Palette.Red($"Unknown mode '{args.Trim()}'.") + " " +
                    Palette.Mute("Use: default | accept-edits | plan | bypass, or /mode with no argument to cycle."))
                    .ConfigureAwait(false);
                return;
            }
            pm.SetMode(target.Value);
            now = target.Value;
        }

        var (glyph, note) = now switch
        {
            PermissionMode.Bypass => (Palette.Red("⚠ bypass"), "auto-allows everything except the dangerous-op floor."),
            PermissionMode.Plan => (Palette.Gold("◆ plan"), "read-only; the agent proposes a plan for approval."),
            PermissionMode.AcceptEdits => (Palette.Cyan("✎ accept-edits"), "auto-allows Edit/Write/NotebookEdit; other tools still ask."),
            _ => (Palette.Cyan("permissions: ask"), "every permission-gated tool asks."),
        };
        await _output.WriteLineAsync($"{glyph} {Palette.Mute("— " + note)}").ConfigureAwait(false);
    }

    private async Task ListWorkflowsAsync()
    {
        var workflows = new WorkflowLoader(_cwd).List();
        if (workflows.Count == 0)
        {
            await _output.WriteLineAsync(
                Palette.Mute($"No workflows found in {Path.Combine(_cwd, ".zdtllm", "workflows")}. ") +
                Palette.Mute("Add a <name>.json there, then run ") + Palette.Cyan("/workflow <name>") + Palette.Mute("."))
                .ConfigureAwait(false);
            return;
        }

        await _output.WriteLineAsync(Palette.BodyBold("Available workflows:")).ConfigureAwait(false);
        foreach (var w in workflows)
        {
            await _output.WriteLineAsync(
                $"  {Palette.GoldBold(w.Name)}  {Palette.Mute($"({w.PhaseCount} phase(s))")}")
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(w.Description))
                await _output.WriteLineAsync($"    {Palette.Mute(w.Description!)}").ConfigureAwait(false);
        }
        await _output.WriteLineAsync(
            Palette.Mute("  Run one with ") + Palette.Cyan("/workflow <name> key=value …")).ConfigureAwait(false);
    }

    private async Task HandleWorkflowCommandAsync(string args, CancellationToken ct)
    {
        if (_subagentRunner is null)
        {
            await _output.WriteLineAsync(
                Palette.Mute("/workflow needs the Agent/subagent runner (interactive mode)."))
                .ConfigureAwait(false);
            return;
        }

        var (name, rest) = SplitFirstToken(args);
        if (string.IsNullOrWhiteSpace(name))
        {
            await _output.WriteLineAsync(Palette.Mute("Usage: /workflow <name> [key=value …]")).ConfigureAwait(false);
            await ListWorkflowsAsync().ConfigureAwait(false);
            return;
        }

        WorkflowDefinition workflow;
        try
        {
            workflow = new WorkflowLoader(_cwd).Load(name);
        }
        catch (WorkflowException ex)
        {
            await _error.WriteLineAsync(Palette.Red($"zdt: {ex.Message}")).ConfigureAwait(false);
            return;
        }

        var wfArgs = ParseKeyValues(rest);

        // Publish a turn CTS so a single Ctrl+C cancels the workflow (like a normal turn).
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _currentTurnCts = turnCts;
        try
        {
            var result = await new WorkflowRunner(_subagentRunner)
                .RunAsync(workflow, wfArgs, _error, turnCts.Token, maxParallel: 0, parentModel: _session.Model)
                .ConfigureAwait(false);

            foreach (var phase in result.Phases)
            {
                await _output.WriteLineAsync(
                    Palette.CyanBold($"◇ {phase.Title}") + " " + Palette.Mute($"({phase.Outputs.Count} output(s))"))
                    .ConfigureAwait(false);
                foreach (var outText in phase.Outputs)
                {
                    if (_richConsole is not null)
                    {
                        _richConsole.Write(MarkdownRenderer.Render(outText));
                        _richConsole.WriteLine();
                    }
                    else
                    {
                        await _output.WriteLineAsync(outText).ConfigureAwait(false);
                    }
                }
            }
            await _output.WriteLineAsync(
                Palette.Cyan("✓") + " " + Palette.Body($"Workflow '{result.Name}' complete."))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested)
        {
            await _error.WriteLineAsync(Palette.Mute("(workflow cancelled)")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync(Palette.Red($"zdt: workflow failed: {ex.Message}")).ConfigureAwait(false);
        }
        finally
        {
            _currentTurnCts = null;
        }
    }

    private static (string First, string Remainder) SplitFirstToken(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return (string.Empty, string.Empty);
        var sp = s.IndexOf(' ');
        return sp < 0 ? (s, string.Empty) : (s[..sp], s[(sp + 1)..].Trim());
    }

    private static IReadOnlyDictionary<string, string> ParseKeyValues(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(s)) return dict;
        foreach (var token in s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            dict[token[..eq].Trim()] = token[(eq + 1)..];
        }
        return dict;
    }

    private async Task HandleContextCommandAsync()
    {
        var ctx = _agent.Context;
        if (ctx is null)
        {
            await _output.WriteLineAsync(
                Palette.Mute(
                    "/context requires a configured context window. Either expose model_info on " +
                    "your LiteLLM proxy (max_input_tokens) or set litellm.contextWindows.<tier> " +
                    "in settings.json."))
                .ConfigureAwait(false);
            return;
        }

        await _output.WriteLineAsync(Palette.BodyBold("Context usage:")).ConfigureAwait(false);
        await _output.WriteLineAsync().ConfigureAwait(false);

        var bar = Palette.Bar(ctx.LastPromptTokens, ctx.ContextWindow, width: 30);
        var totalLine = ctx.LastPromptTokens == 0
            ? $"  {bar}  {Palette.Mute("0%")}   {Palette.Mute("(no turn has run yet — usage populates after the first response)")}"
            : $"  {bar}  {ColorPercent(ctx.UsagePercent, ctx)}   " +
              $"{Palette.Body($"{ctx.LastPromptTokens:N0}")} {Palette.Mute("/")} " +
              $"{Palette.Body($"{ctx.ContextWindow:N0}")} {Palette.Mute("tokens")}";
        await _output.WriteLineAsync(totalLine).ConfigureAwait(false);

        var byRole = ContextManager.EstimateTokensByRole(_session);
        if (byRole.Count > 0)
        {
            await _output.WriteLineAsync().ConfigureAwait(false);
            await _output.WriteLineAsync(Palette.Mute("  by role (estimated, 4 chars/token):")).ConfigureAwait(false);

            foreach (var role in new[] { "system", "user", "assistant", "tool" })
            {
                if (!byRole.TryGetValue(role, out var roleTokens) || roleTokens == 0) continue;
                var roleBar = Palette.Bar(roleTokens, ctx.ContextWindow, width: 20);
                var pct = (double)roleTokens / ctx.ContextWindow * 100;
                await _output.WriteLineAsync(
                        $"    {Palette.Mute(role.PadRight(10))} {roleBar}  " +
                        $"{Palette.Body(FormatTokens(roleTokens).PadLeft(8))}  " +
                        $"{Palette.Mute($"({pct,5:F1}%)")}")
                    .ConfigureAwait(false);
            }
        }

        await _output.WriteLineAsync().ConfigureAwait(false);
        var soft = (int)(ctx.SoftThreshold * ctx.ContextWindow);
        var hard = (int)(ctx.HardThreshold * ctx.ContextWindow);
        await _output.WriteLineAsync($"  {Palette.Mute("model:")} {Palette.GoldBold(_session.Model)}").ConfigureAwait(false);
        await _output.WriteLineAsync(
                $"  {Palette.Gold("/compact recommended at")} {Palette.Body($"{ctx.SoftThreshold * 100:F0}%")} " +
                Palette.Mute($"({soft:N0} tokens)"))
            .ConfigureAwait(false);
        await _output.WriteLineAsync(
                $"  {Palette.Red("auto-compact at")} {Palette.Body($"{ctx.HardThreshold * 100:F0}%")} " +
                Palette.Mute($"({hard:N0} tokens)"))
            .ConfigureAwait(false);
    }

    private static string ColorPercent(int pct, ContextManager ctx)
    {
        var colored = ctx.IsBeyondHardThreshold ? Palette.Red($"{pct}%")
                    : ctx.IsBeyondSoftThreshold ? Palette.Gold($"{pct}%")
                    : Palette.Body($"{pct}%");
        return colored;
    }

    private static string FormatTokens(int n)
    {
        if (n < 1000) return n.ToString();
        if (n < 1_000_000) return $"{n / 1000.0:F1}k";
        return $"{n / 1_000_000.0:F1}M";
    }

    private string SessionDisplay() => _session.IsPersistent ? _session.Id : $"{_session.Id} (ephemeral)";

    private enum SlashOutcome { Continue, Exit }
}

public sealed record ReplOptions;
