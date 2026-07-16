using System.Reflection;
using System.Runtime.InteropServices;
using Spectre.Console;
using Zdtllm.Config;
using Zdtllm.Core;
using Zdtllm.Core.Observers;
using Zdtllm.Cli.Input;
using Zdtllm.Core.Repl;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Workflows;
using Zdtllm.Core.Setup;
using Zdtllm.LiteLLM;
using Zdtllm.Mcp;
using Zdtllm.Permissions;
using Zdtllm.Skills;
using Zdtllm.Tools;

namespace Zdtllm.Cli;

internal static class Program
{
    private const string Url = "https://zer0day.ro";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Windows defaults the console to a non-UTF-8 codepage, which mangles the
            // banner block characters and any non-ASCII content the model emits.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ex is TaskCanceledException && !IsUserCancellation(ex))
        {
            // HttpClient.Timeout-derived OCE that didn't originate from our CT chain. Print a
            // diagnostic message so users can fix their timeoutSeconds instead of seeing the
            // misleading "zdt: cancelled." that suggests they pressed Ctrl+C.
            await Console.Error.WriteLineAsync(
                $"zdt: request timed out (HttpClient.Timeout). " +
                $"Remove litellm.timeoutSeconds from settings.json or raise it. " +
                $"[{ex.GetType().Name}: {ex.Message}]").ConfigureAwait(false);
            return 124; // POSIX timeout exit code
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("zdt: cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Zdtllm.LiteLLM.RateLimitException ex)
        {
            // Distinct exit code so callers can branch on rate-limit vs generic failure
            // without grepping the message. The stream-json path already emitted a structured
            // rate_limit_event + result event before the exception bubbled here, so this
            // print is just for the human (text-mode) path.
            var resetIso = ex.ResetsAtUnix is long unix
                ? DateTimeOffset.FromUnixTimeSeconds(unix).ToString("u")
                : "unknown";
            await Console.Error.WriteLineAsync(
                $"zdt: rate limit exceeded. Try again at {resetIso}.").ConfigureAwait(false);
            return 75; // POSIX EX_TEMPFAIL — convention for transient/retryable failure
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"zdt: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Best-effort heuristic: was the OperationCanceledException triggered by a CT we own
    /// (Ctrl+C / programCts) versus an HttpClient internal timeout? If the OCE's token has
    /// IsCancellationRequested, it was a genuine cancellation; if not, the cancellation
    /// fired from the HttpClient.Timeout path which creates its own internal CTS.
    /// </summary>
    private static bool IsUserCancellation(OperationCanceledException ex) =>
        ex.CancellationToken.IsCancellationRequested;

    private static async Task<int> RunAsync(string[] args)
    {
        var parsed = ArgumentParser.Parse(args);

        if (parsed.ShowVersion) { PrintVersion(); return 0; }
        if (parsed.ShowHelp) { PrintHelp(); return 0; }

        // Both update flags short-circuit before settings/wizard — they don't touch LiteLLM
        // and shouldn't fail just because the user hasn't configured the proxy yet.
        if (parsed.CheckUpdates) return await SelfUpdate.RunCheckUpdatesAsync().ConfigureAwait(false);
        if (parsed.SelfUpdate)   return await SelfUpdate.RunSelfUpdateAsync().ConfigureAwait(false);

        // Anthropic-compat: in -p mode, if no positional query was given but stdin is piped,
        // read the prompt from stdin to EOF. Lets callers like AppSec-Automator do
        // `Process::setInput($prompt)` instead of building a giant argv string.
        if (parsed.PrintMode && string.IsNullOrWhiteSpace(parsed.Query) && Console.IsInputRedirected)
        {
            var piped = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(piped)) parsed.Query = piped.Trim();
        }

        if (parsed.PrintMode && string.IsNullOrWhiteSpace(parsed.Query))
        {
            await Console.Error.WriteLineAsync(
                "zdt -p requires a query (positional arg or piped stdin).").ConfigureAwait(false);
            return 2;
        }

        var cwd = Directory.GetCurrentDirectory();
        var settings = SettingsLoader.LoadEffectiveSettings(cwd);

        if (string.IsNullOrEmpty(settings.LiteLLM.BaseUrl))
        {
            settings = await MaybeRunWizardAsync(parsed, settings, cwd).ConfigureAwait(false);
            if (settings is null) return 0; // user aborted the wizard
        }

        if (string.IsNullOrEmpty(settings.LiteLLM.BaseUrl))
            throw new InvalidOperationException(
                "litellm.baseUrl is still not configured. Run `zdt` interactively (no -p) to launch " +
                "the setup wizard, or edit ~/.zdtllm/settings.json by hand.");

        // Default to InfiniteTimeSpan: an agentic CLI legitimately waits many minutes for the
        // model to produce a complex first chunk, especially with XML mode + a large system
        // prompt. The previous 120 s default surfaced as a confusing "(turn cancelled)" after
        // ~8 min of HttpClient.Timeout firing across MaxRetries+1 attempts. Cancellation flows
        // through the agent's CT chain (Ctrl+C → CancelCurrentTurn) instead.
        // GetModelInfoAsync wraps its own 10 s CTS so this doesn't make /model/info hang.
        var configuredTimeout = settings.LiteLLM.TimeoutSeconds;
        var httpTimeout = configuredTimeout is null or <= 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(configuredTimeout.Value);
        using var http = new HttpClient { Timeout = httpTimeout };
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = settings.LiteLLM.BaseUrl!,
            ApiKey = settings.LiteLLM.ApiKey!,
        });

        var perms = PermissionRuleSet.Build(
            allow: settings.Permissions.Allow,
            ask: settings.Permissions.Ask,
            deny: settings.Permissions.Deny);

        using var fetchHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var skills = parsed.Bare
            ? Array.Empty<SkillDefinition>()
            : new SkillsLoader().Discover(cwd);

        var memoryFile = TryReadMemoryFile(cwd);

        // Interactive-only input plumbing: the message queue (type while the model works) and the
        // console driver that captures those keystrokes AND powers AskUserQuestion's arrow-key
        // picker. Requires a real TTY on both ends — print mode and redirected stdio get neither
        // (the queue stays off; AskUserQuestion isn't registered so the model won't try to ask a
        // human who isn't there).
        var interactive = !parsed.PrintMode
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected;
        UserInputQueue? inputQueue = interactive ? new UserInputQueue() : null;
        ConsoleInput? turnInput = interactive
            ? new ConsoleInput(inputQueue!, AnsiConsole.Console, SlashCommandCatalog.All)
            : null;
        // The rich line editor (multi-line paste, drag & drop, in-line editing) needs an ANSI
        // terminal and can be turned off with ZDT_BASIC_INPUT for anyone whose terminal misbehaves.
        // Capture + the arrow-key pickers still work without it — only idle line editing falls back
        // to the classic Console.ReadLine.
        var basicInput = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZDT_BASIC_INPUT"));
        var richInputSource = turnInput is not null
            && !basicInput
            && AnsiConsole.Console.Profile.Capabilities.Ansi
            ? turnInput
            : null;
        // Plan mode: read-only research + plan-for-approval. Interactive-only (ExitPlanMode needs
        // a human to approve). Starts on when --plan is passed; toggled at runtime with /plan.
        PlanModeState? planMode = interactive ? new PlanModeState(parsed.Plan) : null;

        var registry = new ToolRegistry();
        registry.Register(new ReadTool());
        registry.Register(new WriteTool());
        registry.Register(new EditTool());
        registry.Register(new NotebookEditTool());
        registry.Register(new BashTool(cwd));
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new TodoWriteTool());
        registry.Register(new WebFetchTool(fetchHttp));
        registry.Register(new WebSearchTool());
        if (skills.Count > 0)
            registry.Register(new SkillTool(skills));
        if (turnInput is not null)
            registry.Register(new AskUserQuestionTool(turnInput));
        if (turnInput is not null && planMode is not null)
            registry.Register(new ExitPlanModeTool(planMode, turnInput));

        // MCP servers — parse every --mcp-config in order (later entries override earlier ones
        // for the same server name), spawn each as a stdio subprocess, register its tools as
        // mcp__<server>__<tool>, and keep the manager around so we can DisposeAsync on shutdown.
        var mcpManager = new McpManager(diagnostics: Console.Error);
        var mcpInitTimeoutSeconds =
            parsed.McpInitTimeoutSeconds
            ?? settings.Mcp.InitTimeoutSeconds
            ?? 15;
        await BootMcpServersAsync(parsed.McpConfigs, registry, mcpManager, mcpInitTimeoutSeconds)
            .ConfigureAwait(false);

        // --require-mcp: opt-in fail-fast. Without it, a misbehaving MCP server reports a
        // warning to stderr and the run continues — historical behaviour, kept default-on so
        // existing scripts don't suddenly start exiting non-zero. With it, any failed server
        // aborts the launch BEFORE we burn LiteLLM tokens on a model that won't have the tools
        // it was prompted to use.
        if (parsed.RequireMcp && mcpManager.Statuses.Any(s => !s.Connected))
        {
            var failed = mcpManager.Statuses
                .Where(s => !s.Connected)
                .Select(s => s.Name);
            await Console.Error.WriteLineAsync(
                "zdt: --require-mcp set but the following MCP server(s) failed to start: "
                + string.Join(", ", failed)
                + ". Aborting (see prior error messages for details).")
                .ConfigureAwait(false);
            await mcpManager.DisposeAsync().ConfigureAwait(false);
            return 1;
        }

        var sessionsDir = Path.Combine(cwd, ".zdtllm", "sessions");
        var recent = RecentTracker.ForUserHome();

        // --resume with no id: present the interactive picker (or, when stdin is redirected,
        // fall back to the most-recent session). Resolves to a concrete Resume id so the
        // ResolveSession switch below takes its existing "resume by id" path.
        if (parsed.ResumePicker)
        {
            if (parsed.PrintMode)
            {
                await Console.Error.WriteLineAsync(
                    "zdt: --resume with no session id needs interactive mode. In -p mode pass an " +
                    "explicit id (--resume <uuid>) or use -c to continue the most recent session.")
                    .ConfigureAwait(false);
                return 2;
            }

            var pickedId = ResolveResumePickerId(sessionsDir, recent, cwd);
            if (pickedId is null) return 0; // no sessions, or user cancelled — nothing to resume
            parsed.Resume = pickedId;
            parsed.ResumePicker = false;
        }

        using var session = ResolveSession(parsed, settings, sessionsDir, recent, cwd, defaultPersistent: !parsed.PrintMode);

        var contextManager = await BuildContextManagerAsync(parsed, settings, client, session.Model)
            .ConfigureAwait(false);

        // Vision gating: attach dropped images only when the active model can actually read them.
        // litellm.vision in settings wins; otherwise we auto-detect via /model/info supports_vision.
        if (turnInput is not null)
            turnInput.VisionCapable = await ResolveVisionCapabilityAsync(settings, client, session.Model)
                .ConfigureAwait(false);

        var baseText = ResolveBaseSystemPrompt(parsed);
        var appendText = ResolveAppendSystemPrompt(parsed);
        var additionalDirs = MergeAdditionalDirectories(parsed.AddDirs, settings.Permissions.AdditionalDirectories);

        // Print mode pipes stdout through the shell so a spinner/markdown renderer would
        // mangle redirected output. Interactive mode benefits from rich rendering.
        var richConsole = parsed.PrintMode ? null : AnsiConsole.Console;

        // Compose the agent observer based on --output-format / --verbose. stream-json owns
        // stdout in -p mode (delta events go through the observer); aggregating json captures
        // everything and emits at the end. Verbose stacks on top of any of these via Composite.
        var (observer, aggregator, formatOwnsStdout) = BuildObserver(parsed);

        var agent = new AgentLoop(
            client,
            registry,
            perms,
            new AgentLoopOptions
            {
                Model = session.Model,
                // No default cap — matches claude-cli. Pass --max-turns to enforce a
                // ceiling (CI / scripts that want a hard guard against runaway loops).
                MaxTurns = parsed.MaxTurns ?? int.MaxValue,
                MaxParallel = parsed.MaxParallel ?? 0,
                SkipPermissions = parsed.DangerouslySkipPermissions,
                ToolCallingMode = session.Mode,
                SystemPrompt = SystemPromptComposer.Compose(
                    baseText: baseText,
                    appendText: appendText,
                    memoryFile: memoryFile,
                    additionalDirectories: additionalDirs,
                    skills: skills),
            },
            context: contextManager,
            richConsole: formatOwnsStdout ? null : richConsole,
            observer: observer,
            inputQueue: inputQueue,
            planMode: planMode,
            typeAhead: turnInput);

        // Task tool needs the parent agent to spawn subagents from. Register it AFTER the
        // agent is built — the registry holds a live reference, so the parent agent will see
        // Task on subsequent turns.
        //
        // The model resolver lets per-subagent_type tiering (litellm.subagentModels) apply
        // at dispatch time. The captured snapshot of Models / SubagentModels is fine because
        // settings are read once at startup and never mutate at runtime; if that ever changes,
        // this delegate would need to read live state instead.
        // How subagent activity is surfaced:
        //   • interactive TTY → a navigable "fleet view" (arrow-key switch between live agents when
        //     ≥2 run at once); ZDT_NO_AGENT_VIEW falls back to the tagged stream.
        //   • otherwise → each subagent's activity streamed tagged to stderr (off for a plain
        //     scripted -p run unless --verbose, so automated callers get a clean stderr).
        var agentViewEnabled = interactive
            && turnInput is not null
            && AnsiConsole.Console.Profile.Capabilities.Ansi
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZDT_NO_AGENT_VIEW"));
        AgentFleetView? fleetView = agentViewEnabled ? new AgentFleetView(AnsiConsole.Console, turnInput) : null;
        TextWriter? subagentSink = fleetView is not null
            ? null
            : (parsed.PrintMode && !parsed.Verbose ? null : Console.Error);
        var subagentRunner = new SubagentRunner(agent, subagentSink, fleetView);
        var modelAliases = settings.LiteLLM.Models;
        var subagentOverrides = settings.LiteLLM.SubagentModels;
        var smallFastModel = settings.LiteLLM.SmallFastModel;
        Func<string, string?, string?> tieredModelResolver = (subagentType, _parent) =>
            SubagentModelResolver.Resolve(subagentType, modelAliases, subagentOverrides, smallFastModel);
        registry.Register(new TaskTool(subagentRunner, tieredModelResolver));

        // --tools filter: applied last so it can drop builtins, MCP tools, and Task uniformly.
        if (parsed.AllowedTools.Count > 0)
        {
            ApplyToolsAllowlist(registry, parsed.AllowedTools, mcpManager.Statuses);

            // Empty-registry guard: if the allowlist filtered everything away (typically because
            // every name referred to a failed MCP server), the model has no tools to dispatch and
            // the run is guaranteed to be useless. Exit before spending LiteLLM tokens on it.
            if (registry.All.Length == 0)
            {
                await Console.Error.WriteLineAsync(
                    "zdt: --tools allowlist filtered every tool from the registry — nothing left to dispatch. "
                    + "Common cause: the listed tools all belong to MCP servers that failed to start. Aborting.")
                    .ConfigureAwait(false);
                await mcpManager.DisposeAsync().ConfigureAwait(false);
                return 1;
            }
        }

        // Single CTS feeds program-wide cancellation: a second Ctrl+C exits the process,
        // a first one just halts the current turn (handled inside the REPL via per-turn CTS
        // linked to this one). Print mode short-circuits to "first Ctrl+C exits".
        using var programCts = new CancellationTokenSource();

        // ProcessExit is the last hook the runtime fires before the process is torn down.
        // It runs synchronously even on hard shutdown paths the try/finally below might not
        // see (e.g. an unhandled exception in a background task). We wait up to 2 s for the
        // manager to kill its children — long enough for stdin-close-then-exit, short enough
        // not to block a user's terminal. We do NOT subscribe Console.CancelKeyPress to this
        // because Ctrl+C also flows through the REPL/print handlers below.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { mcpManager.DisposeAsync().AsTask().Wait(2000); } catch { /* swallow */ }
        };

        // Ensure MCP server subprocesses are cleaned up no matter how we exit (normal,
        // exception, Ctrl+C). Wrapping the rest of RunAsync in try/finally is the simplest
        // way to guarantee disposal across both print and interactive paths.
        try
        {

        // --workflow: run a declarative multi-agent workflow one-shot, then exit. Uses the same
        // subagent machinery the Agent tool does. Ctrl+C cancels it.
        if (parsed.Workflow is not null)
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; programCts.Cancel(); };
            return await RunWorkflowAsync(parsed, subagentRunner, cwd, session.Model, programCts.Token)
                .ConfigureAwait(false);
        }

        if (parsed.PrintMode)
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; programCts.Cancel(); };

            // Hook SIGTERM (and SIGQUIT/SIGHUP where applicable) the same way as Ctrl+C —
            // cancel programCts and let AgentLoop's catch (OperationCanceledException) branch
            // emit the terminal {"type":"result","stop_reason":"cancelled"} stream-json event
            // before the process exits. Without this, `timeout` / `kill <pid>` / k8s SIGTERM
            // tear the process down mid-stream, the trailing result event never makes it to
            // disk, and consumers like AppSec-Automator's StreamJsonResult.php record the run
            // as "no_result" (engine exited before emitting a final result event) even though
            // the run actually had useful work to report.
            //
            // PosixSignalRegistration accepts the same SIGTERM constant on Windows in .NET 6+,
            // routed through the console control handler. SIGINT/Ctrl+Break already go through
            // the CancelKeyPress path, so we deliberately don't double-subscribe SIGINT here.
            void OnSignal(PosixSignalContext ctx) { ctx.Cancel = true; programCts.Cancel(); }
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            using var sighup  = PosixSignalRegistration.Create(PosixSignal.SIGHUP,  OnSignal);
            using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);

            // For json / stream-json the model's text goes through the observer; AgentLoop's
            // own output writer must be muted so the same text doesn't double-print to stdout.
            var loopOutput = formatOwnsStdout ? TextWriter.Null : Console.Out;
            try
            {
                await agent.RunTurnAsync(
                    session,
                    parsed.Query!,
                    output: loopOutput,
                    status: Console.Error,
                    ct: programCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (programCts.IsCancellationRequested)
            {
                // The agent loop has already emitted a terminal result event with
                // stop_reason="cancelled" via its catch (OperationCanceledException) branch.
                // Surface a non-zero exit so callers know it didn't run to completion (130 =
                // POSIX SIGINT convention; we don't try to distinguish SIGTERM here since the
                // shape of the result event already records cancellation).
                return 130;
            }

            if (aggregator is not null)
                await aggregator.EmitAsync(Console.Out, programCts.Token).ConfigureAwait(false);
            return 0;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var sessionDisplay = session.IsPersistent ? session.Id : $"{session.Id} (ephemeral)";
        Branding.PrintStartupBanner(AnsiConsole.Console, version, session.Model, session.Mode, sessionDisplay);
        if (planMode?.InPlanMode == true)
            AnsiConsole.MarkupLine(
                $"[bold {Branding.Hex(Branding.BrandGold)}]◆ plan mode[/] " +
                $"[{Branding.Hex(Branding.MutedText)}]— read-only; the agent proposes a plan for your approval. " +
                "Toggle with [/]" + $"[bold {Branding.Hex(Branding.BodyText)}]/plan[/][{Branding.Hex(Branding.MutedText)}].[/]");
        if (turnInput?.VisionCapable == true)
            AnsiConsole.MarkupLine(
                $"[{Branding.Hex(Branding.MutedText)}]🖼  vision: on — drag an image onto the prompt to attach it.[/]");

        var repl = new Repl(
            session,
            agent,
            Console.In,
            Console.Out,
            Console.Error,
            cwd,
            richConsole: richConsole,
            subagentRunner: subagentRunner,
            inputQueue: inputQueue,
            inputCapture: turnInput,
            planMode: planMode,
            richInput: richInputSource);

        // Ctrl+C behaviour, matching claude-cli:
        //   • During a turn  → first press interrupts the turn (keeps the REPL alive) and clears
        //     any pending exit-arm; the turn's cancellation prints "(turn cancelled)".
        //   • At the prompt  → first press arms "press again to exit" (a hint is shown); a second
        //     idle press within the window exits. The arm auto-disarms after 2 s.
        // Interlocked exchange on the arm flag keeps the two Ctrl+C presses from racing.
        var exitArmed = 0;
        using var ctrlCResetTimer = new System.Threading.Timer(
            _ => Interlocked.Exchange(ref exitArmed, 0), null, Timeout.Infinite, Timeout.Infinite);
        Console.CancelKeyPress += (_, e) =>
        {
            if (repl.IsTurnActive)
            {
                // Interrupt the running turn; never exit mid-turn (return to the prompt first).
                Interlocked.Exchange(ref exitArmed, 0);
                e.Cancel = true;
                repl.CancelCurrentTurn();
                return;
            }

            // Idle: two presses to exit.
            if (Interlocked.Exchange(ref exitArmed, 1) == 1)
            {
                // Console reads don't reliably unblock on cancellation (Windows), so print the
                // farewell here (idempotent — the RunAsync finally won't double it) and let the
                // runtime tear us down.
                repl.PrintFarewell();
                e.Cancel = false;
                programCts.Cancel();
                return;
            }

            e.Cancel = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine("  (press Ctrl+C again to exit)");
            ctrlCResetTimer.Change(2000, Timeout.Infinite);
        };

        return await repl.RunAsync(parsed.Query, programCts.Token).ConfigureAwait(false);
        }
        finally
        {
            fleetView?.Dispose();
            turnInput?.Dispose();
            await mcpManager.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compose the IAgentObserver based on --output-format / --verbose. Returns:
    ///   observer        — the actual sink wired into AgentLoop (null when nothing requested)
    ///   aggregator      — non-null only for --output-format=json (CLI flushes it at exit)
    ///   formatOwnsStdout — true when stdout is reserved for the format (stream-json's NDJSON
    ///                      lines or json's single object) and the loop's text writer must be muted.
    /// </summary>
    private static (IAgentObserver? observer, AggregatingJsonObserver? aggregator, bool formatOwnsStdout)
        BuildObserver(ParsedArgs parsed)
    {
        var format = (parsed.OutputFormat ?? "text").ToLowerInvariant();
        var observers = new List<IAgentObserver>();
        AggregatingJsonObserver? aggregator = null;
        var formatOwnsStdout = false;

        switch (format)
        {
            case "text":
                break;
            case "stream-json":
                if (!parsed.PrintMode)
                {
                    Console.Error.WriteLine("zdt: --output-format=stream-json is only honoured with -p; falling back to text.");
                    break;
                }
                observers.Add(new StreamJsonObserver(Console.Out));
                formatOwnsStdout = true;
                break;
            case "json":
                if (!parsed.PrintMode)
                {
                    Console.Error.WriteLine("zdt: --output-format=json is only honoured with -p; falling back to text.");
                    break;
                }
                aggregator = new AggregatingJsonObserver();
                observers.Add(aggregator);
                formatOwnsStdout = true;
                break;
            default:
                Console.Error.WriteLine($"zdt: unknown --output-format '{format}'. Valid: text | json | stream-json.");
                break;
        }

        if (parsed.Verbose)
            observers.Add(new VerboseObserver(Console.Error));

        IAgentObserver? observer = observers.Count switch
        {
            0 => null,
            1 => observers[0],
            _ => new CompositeObserver(observers),
        };
        return (observer, aggregator, formatOwnsStdout);
    }

    /// <summary>
    /// Drop every tool whose name isn't in <paramref name="allowed"/>. Logs which tools
    /// got removed (helpful when a typo silently strips a feature) and warns if the
    /// allowlist mentions a tool that isn't registered. <paramref name="mcpStatuses"/>
    /// lets the warning distinguish a real typo from "the MCP server that owns this
    /// tool failed to start" — without that, an operator sees "typo? case mismatch?"
    /// and burns time double-checking spelling when the actual cause is upstream.
    /// </summary>
    private static void ApplyToolsAllowlist(
        ToolRegistry registry,
        IReadOnlyList<string> allowed,
        IReadOnlyList<McpServerStatus> mcpStatuses)
    {
        var keep = new HashSet<string>(allowed, StringComparer.Ordinal);
        var present = registry.All.Select(t => t.Schema.Name).ToHashSet(StringComparer.Ordinal);
        var failedMcpServers = new HashSet<string>(
            mcpStatuses.Where(s => !s.Connected).Select(s => s.Name),
            StringComparer.Ordinal);

        foreach (var requested in allowed)
        {
            if (present.Contains(requested)) continue;

            // mcp__<server>__<tool>: if <server> failed to start, the misleading "typo?
            // case mismatch?" hint sends operators down the wrong rabbit hole.
            if (TryExtractMcpServerName(requested, out var serverName)
                && failedMcpServers.Contains(serverName))
            {
                Console.Error.WriteLine(
                    $"zdt: --tools: '{requested}' unavailable because MCP server '{serverName}' "
                    + "failed to start (see prior error).");
            }
            else
            {
                Console.Error.WriteLine(
                    $"zdt: --tools: '{requested}' is not a registered tool (typo? case mismatch?).");
            }
        }

        var toRemove = present.Where(n => !keep.Contains(n)).ToList();
        foreach (var name in toRemove) registry.Remove(name);

        if (toRemove.Count > 0)
            Console.Error.WriteLine($"zdt: --tools allowlist active — kept {registry.All.Length}, dropped {toRemove.Count}.");
    }

    /// <summary>
    /// Parse <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> into the server segment. Returns false
    /// for any non-MCP name or a malformed prefix so callers don't accidentally surface a
    /// "server failed" message for a vanilla tool typo.
    /// </summary>
    private static bool TryExtractMcpServerName(string toolName, out string server)
    {
        server = string.Empty;
        const string prefix = "mcp__";
        if (!toolName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = toolName.AsSpan(prefix.Length);
        var sep = rest.IndexOf("__", StringComparison.Ordinal);
        if (sep <= 0) return false;
        server = rest[..sep].ToString();
        return true;
    }

    /// <summary>
    /// Fans observer events out to a list of underlying observers. Each notification fires
    /// every observer sequentially; failures in one don't short-circuit the others (the
    /// AgentLoop's SafeNotifyAsync wrapper already swallows observer exceptions, so failures
    /// here just mean the failing observer skipped this event).
    /// </summary>
    private sealed class CompositeObserver : IAgentObserver
    {
        private readonly IReadOnlyList<IAgentObserver> _inner;
        public CompositeObserver(IReadOnlyList<IAgentObserver> inner) { _inner = inner; }
        public async Task OnTextDeltaAsync(string text, CancellationToken ct)
        { foreach (var o in _inner) await o.OnTextDeltaAsync(text, ct).ConfigureAwait(false); }
        public async Task OnToolCallAsync(string toolName, string argumentsJson, CancellationToken ct)
        { foreach (var o in _inner) await o.OnToolCallAsync(toolName, argumentsJson, ct).ConfigureAwait(false); }
        public async Task OnToolResultAsync(string toolName, string content, bool isError, TimeSpan duration, CancellationToken ct)
        { foreach (var o in _inner) await o.OnToolResultAsync(toolName, content, isError, duration, ct).ConfigureAwait(false); }
        public async Task OnFinalAsync(string finalText, int turns, int? promptTokens, int? completionTokens, CancellationToken ct)
        { foreach (var o in _inner) await o.OnFinalAsync(finalText, turns, promptTokens, completionTokens, ct).ConfigureAwait(false); }
        public async Task OnAssistantTurnAsync(
            string text,
            System.Collections.Immutable.ImmutableArray<Zdtllm.LiteLLM.ToolCall> toolCalls,
            string model, int? inputTokens, int? outputTokens, CancellationToken ct)
        { foreach (var o in _inner) await o.OnAssistantTurnAsync(text, toolCalls, model, inputTokens, outputTokens, ct).ConfigureAwait(false); }
        public async Task OnResultAsync(
            string subtype, bool isError, int numTurns, string? stopReason, string? resultText,
            int totalInputTokens, int totalOutputTokens, CancellationToken ct,
            bool formatBreakdown = false, int toolErrorCount = 0)
        { foreach (var o in _inner) await o.OnResultAsync(subtype, isError, numTurns, stopReason, resultText, totalInputTokens, totalOutputTokens, ct, formatBreakdown, toolErrorCount).ConfigureAwait(false); }
        public async Task OnRateLimitedAsync(string status, long? resetsAtUnix, CancellationToken ct)
        { foreach (var o in _inner) await o.OnRateLimitedAsync(status, resetsAtUnix, ct).ConfigureAwait(false); }
        public async Task OnFormatBreakdownAsync(string details, CancellationToken ct)
        { foreach (var o in _inner) await o.OnFormatBreakdownAsync(details, ct).ConfigureAwait(false); }
    }

    /// <summary>
    /// Parse every --mcp-config file, merge them in flag order, spawn the servers, and register
    /// their tools. Errors are reported per-server to stderr but never fatal — a misbehaving
    /// MCP server should not block the rest of the agent from starting.
    /// </summary>
    private static async Task BootMcpServersAsync(
        IReadOnlyList<string> configPaths,
        ToolRegistry registry,
        McpManager manager,
        int initTimeoutSeconds)
    {
        if (configPaths.Count == 0) return;

        var merged = new List<McpServerConfig>();
        foreach (var path in configPaths)
        {
            try
            {
                var parsed = McpConfigParser.ParseFile(path);
                merged = McpConfigParser.Merge(merged, parsed).ToList();
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"zdt: --mcp-config: {ex.Message}").ConfigureAwait(false);
            }
        }

        if (merged.Count == 0) return;

        await manager.StartAndRegisterAsync(
            merged,
            registry,
            handshakeTimeout: TimeSpan.FromSeconds(initTimeoutSeconds),
            ct: CancellationToken.None).ConfigureAwait(false);

        foreach (var status in manager.Statuses)
        {
            if (status.Connected)
            {
                Console.Error.WriteLine(
                    $"zdt: mcp[{status.Name}] connected ({status.ServerInfo}, {status.ToolCount} tool(s))");
            }
            else
            {
                Console.Error.WriteLine(
                    $"zdt: mcp[{status.Name}] failed: {status.ErrorMessage}");
            }
        }
    }

    /// <summary>
    /// Resolves which Session to run against, given the parsed flags. Honours
    /// --session-id (create-or-resume), -c (resume most recent for cwd), -r
    /// (resume by id), otherwise builds an ephemeral non-persistent session.
    /// Persistent sessions update the recent-tracker so a future -c finds them.
    /// </summary>
    private static string? TryReadMemoryFile(string cwd)
    {
        var path = Path.Combine(cwd, "ZDTLLM.md");
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>
    /// Resolve the BASE system prompt according to flags. --system-prompt-file takes
    /// precedence over --system-prompt; either replaces the default. With neither, the
    /// AgentLoopOptions default is used.
    /// </summary>
    private static string ResolveBaseSystemPrompt(ParsedArgs parsed)
    {
        if (!string.IsNullOrEmpty(parsed.SystemPromptFile))
            return File.ReadAllText(parsed.SystemPromptFile);
        if (!string.IsNullOrEmpty(parsed.SystemPrompt))
            return parsed.SystemPrompt;
        return AgentLoopOptions.DefaultSystemPrompt;
    }

    private static string? ResolveAppendSystemPrompt(ParsedArgs parsed)
    {
        if (!string.IsNullOrEmpty(parsed.AppendSystemPromptFile))
            return File.ReadAllText(parsed.AppendSystemPromptFile);
        return parsed.AppendSystemPrompt;
    }

    private static IReadOnlyList<string> MergeAdditionalDirectories(
        IReadOnlyList<string> fromCli,
        IReadOnlyList<string> fromSettings)
    {
        if (fromCli.Count == 0 && fromSettings.Count == 0) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(fromCli.Count + fromSettings.Count);
        foreach (var d in fromSettings)
            if (!string.IsNullOrWhiteSpace(d) && seen.Add(d)) result.Add(d);
        foreach (var d in fromCli)
            if (!string.IsNullOrWhiteSpace(d) && seen.Add(d)) result.Add(d);
        return result;
    }

    /// <summary>
    /// Resolve a context window for the active model, in priority order:
    ///   1. settings.litellm.contextWindows[<alias>] — explicit user config wins.
    ///   2. LiteLLM /model/info → model_info.max_input_tokens (or max_tokens) for
    ///      the resolved model name.
    ///   3. null → no context tracking, /context tells the user how to fix it.
    /// </summary>
    /// <summary>
    /// Run a declarative workflow one-shot: load it, execute every phase (progress to stderr),
    /// print the final phase's output to stdout for piping. Returns a POSIX-ish exit code.
    /// </summary>
    private static async Task<int> RunWorkflowAsync(
        ParsedArgs parsed, ISubagentRunner runner, string cwd, string model, CancellationToken ct)
    {
        var loader = new WorkflowLoader(cwd);
        WorkflowDefinition workflow;
        try
        {
            workflow = loader.Load(parsed.Workflow!);
        }
        catch (WorkflowException ex)
        {
            await Console.Error.WriteLineAsync($"zdt: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var args = ParseWorkflowArgs(parsed.WorkflowArgs);
        await Console.Error.WriteLineAsync(
            $"zdt: running workflow '{workflow.Name}' — {workflow.Phases.Count} phase(s)")
            .ConfigureAwait(false);

        WorkflowResult result;
        try
        {
            result = await new WorkflowRunner(runner)
                .RunAsync(workflow, args, Console.Error, ct, maxParallel: parsed.MaxParallel ?? 0, parentModel: model)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("zdt: workflow cancelled.").ConfigureAwait(false);
            return 130;
        }

        // stdout carries the final phase's text (pipe-friendly). Intermediate phases go to stderr.
        await Console.Out.WriteLineAsync(result.FinalOutput).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Parse repeatable <c>--arg key=value</c> tokens into a case-sensitive dictionary.</summary>
    internal static IReadOnlyDictionary<string, string> ParseWorkflowArgs(IReadOnlyList<string> raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in raw)
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue; // ignore malformed entries (no key)
            dict[token[..eq].Trim()] = token[(eq + 1)..];
        }
        return dict;
    }

    /// <summary>
    /// Decide whether the active model accepts images. Priority: an explicit <c>litellm.vision</c>
    /// setting, else LiteLLM's <c>/model/info</c> <c>supports_vision</c> for the resolved model,
    /// else false (conservative — the user asked for images gated on capable models). Best-effort:
    /// a /model/info failure just means "no vision," never a crash.
    /// </summary>
    private static async Task<bool> ResolveVisionCapabilityAsync(
        EffectiveSettings settings, LiteLLMClient client, string resolvedModel)
    {
        if (settings.LiteLLM.Vision is bool configured) return configured;
        try
        {
            var infos = await client.GetModelInfoAsync().ConfigureAwait(false);
            var match = infos.FirstOrDefault(m =>
                string.Equals(m.ModelName, resolvedModel, StringComparison.Ordinal));
            return match?.SupportsVision ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ContextManager?> BuildContextManagerAsync(
        ParsedArgs parsed,
        EffectiveSettings settings,
        LiteLLMClient client,
        string resolvedModel)
    {
        int? window = null;

        var alias = parsed.Model ?? settings.Model;
        if (!string.IsNullOrEmpty(alias)
            && settings.LiteLLM.ContextWindows.TryGetValue(alias, out var fromSettings)
            && fromSettings > 0)
        {
            window = fromSettings;
        }

        if (window is null)
        {
            var modelInfos = await client.GetModelInfoAsync().ConfigureAwait(false);
            var match = modelInfos.FirstOrDefault(m =>
                string.Equals(m.ModelName, resolvedModel, StringComparison.Ordinal));
            if (match?.EffectiveContextWindow is int apiWindow && apiWindow > 0)
                window = apiWindow;
        }

        // Default to 200k when neither settings nor /model/info provide a value. Reasoning:
        // most modern frontier-class models ship with 128k+ contexts (Claude/GPT-4/Qwen3/etc),
        // so 200k is a safe upper bound that keeps ContextManager active by default. Users with
        // smaller-window deployments (e.g. vLLM with --max-model-len 16384) MUST set
        // litellm.contextWindows.<alias> in settings.json — otherwise auto-compact won't fire
        // before LiteLLM rejects the request with a 400.
        window ??= 200_000;

        var mediumName = settings.LiteLLM.Models.TryGetValue("medium", out var m) && !string.IsNullOrEmpty(m)
            ? m
            : resolvedModel;

        return new ContextManager(window.Value, mediumName);
    }

    private static async Task<EffectiveSettings?> MaybeRunWizardAsync(
        ParsedArgs parsed,
        EffectiveSettings settings,
        string cwd)
    {
        if (parsed.NoWizard || parsed.PrintMode)
        {
            throw new InvalidOperationException(
                "litellm.baseUrl is not configured. Run `zdt` interactively (no -p) to launch the " +
                "setup wizard, or write ~/.zdtllm/settings.json by hand. Pass --no-wizard to keep " +
                "this error in scripts.");
        }

        using var wizardHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var wizard = new SetupWizard(Console.In, Console.Out, wizardHttp);
        var result = await wizard.RunAsync(SetupWizard.DefaultUserSettingsPath()).ConfigureAwait(false);
        if (!result.UserConfirmed) return null;

        // Re-load with the freshly-written file in place.
        return SettingsLoader.LoadEffectiveSettings(cwd);
    }

    /// <summary>
    /// Resolve which session id the user wants to resume when <c>--resume</c> was passed with
    /// no id. Presents an arrow-key Spectre picker of the most recent conversations for this
    /// project. Returns the chosen id, or null when there's nothing to resume / the user
    /// cancelled. When stdin is redirected (can't drive an interactive prompt) it falls back
    /// to the most-recent session for the cwd so scripts still get sensible behaviour.
    /// </summary>
    private static string? ResolveResumePickerId(string sessionsDir, RecentTracker recent, string cwd)
    {
        var summaries = new SessionCatalog(sessionsDir).List(limit: 25);
        if (summaries.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[{Branding.Hex(Branding.MutedText)}]No saved conversations for this project yet. " +
                "Start one with [/]" + $"[bold {Branding.Hex(Branding.BodyText)}]zdt[/]" +
                $"[{Branding.Hex(Branding.MutedText)}].[/]");
            return null;
        }

        // Non-interactive stdin (piped / redirected) can't drive SelectionPrompt. Fall back to
        // the recent-tracker's most-recent id, or the newest file if the tracker is empty.
        if (Console.IsInputRedirected)
            return recent.GetMostRecentForCwd(cwd) ?? summaries[0].Id;

        var choices = summaries.ToList();
        var prompt = new SelectionPrompt<SessionSummary>()
            .Title($"[bold {Branding.Hex(Branding.BrandCyan)}]Resume a conversation[/] " +
                   $"[{Branding.Hex(Branding.MutedText)}](↑/↓ to move, Enter to select, Esc/Ctrl+C to cancel)[/]")
            .PageSize(12)
            .HighlightStyle(new Style(Branding.BrandCyan))
            .UseConverter(FormatSessionChoice)
            .AddChoices(choices);

        SessionSummary selected;
        try
        {
            selected = AnsiConsole.Prompt(prompt);
        }
        catch (Exception)
        {
            // SelectionPrompt throws if the terminal can't support interaction after all
            // (e.g. NO_COLOR dumb terminals). Degrade to most-recent rather than crashing.
            return recent.GetMostRecentForCwd(cwd) ?? summaries[0].Id;
        }

        return selected.Id;
    }

    /// <summary>Render one session row for the resume picker: title · relative age · model.</summary>
    private static string FormatSessionChoice(SessionSummary s)
    {
        var title = string.IsNullOrWhiteSpace(s.Name) ? s.Title : s.Name;
        title = string.IsNullOrWhiteSpace(title) ? "(no messages)" : title;
        const int cap = 64;
        if (title!.Length > cap) title = string.Concat(title.AsSpan(0, cap), "…");

        var age = FormatRelativeAge(s.LastModified);
        var turns = s.AssistantTurns == 1 ? "1 turn" : $"{s.AssistantTurns} turns";
        return
            $"[{Branding.Hex(Branding.BodyText)}]{Markup.Escape(title)}[/]  " +
            $"[{Branding.Hex(Branding.MutedText)}]· {age} · {turns} · {Markup.Escape(s.Model)}[/]";
    }

    private static string FormatRelativeAge(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static Session ResolveSession(
        ParsedArgs parsed,
        EffectiveSettings settings,
        string sessionsDir,
        RecentTracker recent,
        string cwd,
        bool defaultPersistent)
    {
        if (parsed.SessionId is not null)
        {
            var path = Path.Combine(sessionsDir, $"{parsed.SessionId}.jsonl");
            if (File.Exists(path))
            {
                var session = Session.Resume(SessionStore.OpenForResume(sessionsDir, parsed.SessionId));
                recent.Mark(cwd, session.Id);
                return session;
            }
            else
            {
                var (model, mode) = ResolveModelAndMode(parsed, settings);
                var store = SessionStore.Create(sessionsDir, parsed.SessionId);
                var session = Session.NewPersistent(store, model, name: null, mode);
                recent.Mark(cwd, session.Id);
                return session;
            }
        }

        if (parsed.Continue)
        {
            var recentId = recent.GetMostRecentForCwd(cwd)
                ?? throw new InvalidOperationException(
                    "No recent session for this directory. Start a new session first or pass --session-id.");
            var session = Session.Resume(SessionStore.OpenForResume(sessionsDir, recentId));
            return session;
        }

        if (parsed.Resume is not null)
        {
            var session = Session.Resume(SessionStore.OpenForResume(sessionsDir, parsed.Resume));
            recent.Mark(cwd, session.Id);
            return session;
        }

        // Default path — interactive defaults to persistent (so `-c` next time finds it),
        // print mode defaults to ephemeral (no on-disk side effect for one-shot queries).
        var (m, mo) = ResolveModelAndMode(parsed, settings);
        if (defaultPersistent)
        {
            var store = SessionStore.Create(sessionsDir);
            var newSession = Session.NewPersistent(store, m, name: null, mo);
            recent.Mark(cwd, newSession.Id);
            return newSession;
        }
        return Session.NewEphemeral(m, mo);
    }

    internal static (string Model, ToolCallingMode Mode) ResolveModelAndMode(
        ParsedArgs parsed,
        EffectiveSettings settings)
    {
        var modelAlias = parsed.Model ?? settings.Model;
        if (string.IsNullOrEmpty(modelAlias))
            throw new InvalidOperationException(
                "No model specified. Pass --model or set 'model' in .zdtllm/settings.json.");

        var modelName = settings.LiteLLM.Models.TryGetValue(modelAlias, out var resolved)
            ? resolved
            : modelAlias;

        // Mode resolution prioritises an explicit choice (CLI flag, then settings.json). When
        // neither is set, infer from the model name: open-weights chat templates (qwen / glm /
        // deepseek / hermes / kimi / yi / mistral-nemo) generally don't expose OpenAI-shaped
        // function-calling on LiteLLM and fall back to text — which means Native mode would
        // silently drop tool calls every turn. Auto-switching to XML and emitting a one-line
        // stderr note keeps these models working out of the box without forcing every user to
        // discover the toolCallingMode setting.
        var explicitMode = parsed.ToolCallingMode ?? settings.LiteLLM.ToolCallingMode;
        ToolCallingMode mode;
        if (string.IsNullOrEmpty(explicitMode) && LooksLikeXmlOnlyModel(modelName))
        {
            Console.Error.WriteLine(
                $"zdt: auto-selecting --tool-calling xml for model '{modelName}' " +
                "(set 'toolCallingMode' in settings.json or pass --tool-calling native to override).");
            mode = ToolCallingMode.Xml;
        }
        else
        {
            mode = ToolCallingModeParse.FromString(explicitMode, fallback: ToolCallingMode.Native);
        }

        return (modelName, mode);
    }

    /// <summary>
    /// Heuristic: does <paramref name="modelName"/> look like an open-weights model that
    /// doesn't reliably support OpenAI-shaped native tool-calling on LiteLLM? Matched against
    /// substrings (case-insensitive) so versioned ids like
    /// <c>Qwen/Qwen3-Coder-30B-A3B-Instruct</c> or <c>glm-5.1:cloud</c> still trigger.
    /// Wrong matches just push a model that supports both modes onto XML — slightly more
    /// verbose tool calls, no functional regression. Missed matches leave the existing
    /// "explicit-or-native" behaviour, which is the conservative default.
    /// </summary>
    internal static bool LooksLikeXmlOnlyModel(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        ReadOnlySpan<string> markers =
        [
            "qwen", "glm", "deepseek", "hermes", "kimi", "yi-", "nemo",
        ];
        foreach (var m in markers)
        {
            if (modelName.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Branding.PrintVersion(AnsiConsole.Console, version);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"zdtllmcli — CLI LLM Agent, backed by LiteLLM. ({Url})");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  zdt                            interactive REPL (new persistent session)");
        Console.WriteLine("  zdt \"<query>\"                interactive REPL, kicked off with <query>");
        Console.WriteLine("  zdt -p \"<query>\"              one-shot print mode (ephemeral by default)");
        Console.WriteLine("  zdt -c                         interactive, resume most recent session");
        Console.WriteLine("  zdt -r                         interactive, pick a recent session to resume");
        Console.WriteLine("  zdt -r <uuid>                  interactive, resume the given session");
        Console.WriteLine();
        Console.WriteLine("FLAGS:");
        Console.WriteLine("  -p, --print                    print mode (one-shot, exit)");
        Console.WriteLine("  --model <alias|name>           model alias (light/medium/heavy) or full name");
        Console.WriteLine("                                 subagents inherit this unless litellm.subagentModels remaps them");
        Console.WriteLine("                                 (e.g. {\"code-reviewer\": \"light\"} routes the read-only profile to the cheap tier)");
        Console.WriteLine("  --max-turns <n>                cap agent loop iterations (no limit by default)");
        Console.WriteLine("  --max-parallel <n>             cap concurrent tool calls in a parallel batch (0 = unlimited)");
        Console.WriteLine("  --dangerously-skip-permissions auto-allow tools that would otherwise prompt");
        Console.WriteLine("  --no-wizard                    skip the first-run setup wizard");
        Console.WriteLine("  --bare                         skip auto-discovery of skills");
        Console.WriteLine("  --plan                         start in plan mode (read-only; propose a plan before changes)");
        Console.WriteLine("  --workflow <name>              run a declarative workflow from .zdtllm/workflows/, then exit");
        Console.WriteLine("  --arg key=value                input for --workflow (repeatable; list values are comma-separated)");
        Console.WriteLine("  --tool-calling <native|xml>    transport for tool calls (default: native)");
        Console.WriteLine("  --system-prompt <text>         replace the default system prompt with <text>");
        Console.WriteLine("  --system-prompt-file <path>    replace the default system prompt with file contents");
        Console.WriteLine("  --append-system-prompt <text>  append <text> after the default/replaced prompt");
        Console.WriteLine("  --append-system-prompt-file <p>  append file contents after the default/replaced prompt");
        Console.WriteLine("  --add-dir <path>               add an extra accessible directory (repeatable)");
        Console.WriteLine("  --mcp-config <path>            load MCP server config from a JSON file (repeatable, last wins per server)");
        Console.WriteLine("  --mcp-init-timeout-seconds <n> per-server MCP handshake timeout (default 15; raise for slow-booting servers)");
        Console.WriteLine("  --require-mcp                  exit non-zero if any --mcp-config server fails to start (off by default;");
        Console.WriteLine("                                 no-op when --mcp-config wasn't passed — see docs)");
        Console.WriteLine("  --verbose                      trace tool calls + results to stderr (durations, args/preview)");
        Console.WriteLine("  --output-format <fmt>          text (default) | json | stream-json — only honoured with -p");
        Console.WriteLine("  --tools <names...>             allowlist of tool names (space- or comma-separated, e.g. Read Glob Grep)");
        Console.WriteLine("  --allowed-tools <names...>     alias for --tools (claude-cli compat)");
        Console.WriteLine("  --session-id <uuid>            create or resume a persistent session at this id");
        Console.WriteLine("  -c, --continue                 resume the most recent session for this directory");
        Console.WriteLine("  -r, --resume [uuid]            resume a session; with no uuid, pick from recent ones interactively");
        Console.WriteLine("  --version                      print version and exit");
        Console.WriteLine("  --check-updates                check GitHub for a newer release and exit");
        Console.WriteLine("  --self-update                  download + install the latest release in place");
        Console.WriteLine("  -h, --help                     show this help");
        Console.WriteLine();
        Console.WriteLine("ENV:");
        Console.WriteLine("  ZDT_BASE_URL                   LiteLLM proxy URL (overrides litellm.baseUrl)");
        Console.WriteLine("  ZDT_API_KEY                    LiteLLM proxy API key (overrides litellm.apiKey)");
        Console.WriteLine("  ZDT_DEFAULT_HEAVY_MODEL        model id for the 'heavy' tier (overrides litellm.models.heavy)");
        Console.WriteLine("  ZDT_DEFAULT_MEDIUM_MODEL       model id for the 'medium' tier (overrides litellm.models.medium)");
        Console.WriteLine("  ZDT_DEFAULT_LIGHT_MODEL        model id for the 'light' tier (overrides litellm.models.light)");
        Console.WriteLine("  ZDT_SMALL_FAST_MODEL           default model for read-only subagents (code-reviewer, explore)");
        Console.WriteLine();
        Console.WriteLine($"More at {Url}");
    }

}
