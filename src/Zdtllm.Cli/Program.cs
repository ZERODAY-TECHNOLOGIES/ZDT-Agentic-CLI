using System.Reflection;
using System.Runtime.InteropServices;
using Spectre.Console;
using Zdtllm.Config;
using Zdtllm.Core;
using Zdtllm.Core.Agents;
using Zdtllm.Core.Observers;
using Zdtllm.Cli.Input;
using Zdtllm.Cli.Tui;
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

        // Apply the settings.json "env" block to this process's environment. It was parsed and
        // ${VAR}-expanded during load but nothing consumed it. Setting it here means Bash-tool
        // subprocesses (and any child process) inherit committed env — proxy vars, tokens, PATH
        // additions — without the operator having to export them in every shell. We do NOT clobber
        // a variable that is already set in the real environment (an explicit shell export wins).
        foreach (var kv in settings.Env)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(kv.Key)))
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }

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

        // Resolve the alias to the real model id once, so the per-model tuning heuristics below all
        // see the concrete id (e.g. "Qwen3.6-35B-A3B-…") rather than a short alias.
        var tuningAlias = parsed.Model ?? settings.Model;
        var tuningModel = tuningAlias is not null && settings.LiteLLM.Models.TryGetValue(tuningAlias, out var rm)
            ? rm : tuningAlias;

        // GLM-5.2 is reasoning-first: with reasoning_effort unset the server thinks at its default
        // 'max' tier on EVERY turn (including trivial tool-continuation turns). Default it to 'high'
        // — the documented guidance — for a GLM model when the user hasn't pinned it. Any explicit
        // litellm.reasoningEffort still wins; non-GLM models are unaffected (stays null → omitted).
        var reasoningEffort = settings.LiteLLM.ReasoningEffort;
        if (reasoningEffort is null && Zdtllm.Core.ModelHeuristics.LooksLikeGlm(tuningModel))
            reasoningEffort = "high";

        // Qwen3 sampling profile. Critical for local llama.cpp routes: llama.cpp does NOT read the
        // model's HF generation_config.json, and its built-in sampler defaults (temp 0.8 / top_p 0.9 /
        // top_k 40 / min_p 0.05) are wrong for Qwen3 — they degrade quality and trigger the repetition
        // loops the A3B MoE models are prone to. Send Qwen3's documented thinking/coding profile
        // explicitly. Only fills a knob the user left unset (each ??= respects an explicit litellm.*
        // value); non-Qwen3 models are untouched (stays null → omitted → byte-identical body).
        var temperature = settings.LiteLLM.Temperature;
        var topP = settings.LiteLLM.TopP;
        var topK = settings.LiteLLM.TopK;
        var minP = settings.LiteLLM.MinP;
        if (Zdtllm.Core.ModelHeuristics.LooksLikeQwen3(tuningModel))
        {
            temperature ??= 0.6;
            topP ??= 0.95;
            topK ??= 20;
            minP ??= 0;
        }

        // Stream idle watchdog: the HTTP timeout is intentionally infinite (a slow model must not be
        // cut off mid-generation), so without this a wedged/stalled backend hangs the CLI forever.
        // null → the client's built-in 240s default; <= 0 → disabled (wait forever, legacy behaviour).
        var streamIdleSeconds = settings.LiteLLM.StreamIdleTimeoutSeconds;
        var streamIdle =
            streamIdleSeconds is null ? TimeSpan.FromSeconds(240) :
            streamIdleSeconds.Value <= 0 ? Timeout.InfiniteTimeSpan :
            TimeSpan.FromSeconds((double)streamIdleSeconds.Value);

        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = settings.LiteLLM.BaseUrl!,
            ApiKey = settings.LiteLLM.ApiKey!,
            StreamIdleTimeout = streamIdle,
            // Optional request-shaping passthroughs (all null/empty unless set in settings.json or
            // supplied by a per-model default above). For GLM-5.2: reasoningEffort defaults to "high",
            // temperature stays UNSET (GLM is trained at 1.0 — do not lower it). For Qwen3: temp/top_p/
            // top_k/min_p get the profile above. frequency/presence penalties are opt-in anti-repetition
            // levers. Per-model-safe: unset → omitted → byte-identical body.
            ReasoningEffort = reasoningEffort,
            Temperature = temperature,
            TopP = topP,
            TopK = topK,
            MinP = minP,
            MaxTokens = settings.LiteLLM.MaxTokens,
            FrequencyPenalty = settings.LiteLLM.FrequencyPenalty,
            PresencePenalty = settings.LiteLLM.PresencePenalty,
            ExtraParams = settings.LiteLLM.ExtraParams,
        });

        var perms = PermissionRuleSet.Build(
            allow: settings.Permissions.Allow,
            ask: settings.Permissions.Ask,
            deny: settings.Permissions.Deny);

        using var fetchHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var skills = parsed.Bare
            ? Array.Empty<SkillDefinition>()
            : new SkillsLoader().Discover(cwd);

        // User-defined slash commands from .zdtllm/commands/*.md. Merged into the picker catalog (so
        // both drivers advertise them) and handed to the REPL, which expands + runs them as turns.
        var customCommands = parsed.Bare
            ? (IReadOnlyList<Zdtllm.Core.Commands.CustomCommand>)Array.Empty<Zdtllm.Core.Commands.CustomCommand>()
            : new Zdtllm.Core.Commands.CommandLoader().Discover(cwd);
        var slashCatalog = customCommands.Count == 0
            ? SlashCommandCatalog.All
            : SlashCommandCatalog.All
                .Concat(customCommands.Select(c => new SlashCommandInfo("/" + c.Name, c.Description)))
                .ToList();

        // Project subagents (team mode): discovered from .zdtllm/agents/*.md at startup and held in a
        // live registry the /team wizard can extend mid-session. --bare skips them like skills/commands.
        var teamAgents = new TeamAgentRegistry(
            parsed.Bare
                ? Array.Empty<AgentDefinition>()
                : new AgentDefinitionLoader().Discover(cwd));

        var memoryFile = TryReadMemoryFile(cwd);

        // Interactive-only input plumbing: the message queue (type while the model works) and the
        // console driver that captures those keystrokes AND powers AskUserQuestion's arrow-key
        // picker. Requires a real TTY on both ends — print mode and redirected stdio get neither
        // (the queue stays off; AskUserQuestion isn't registered so the model won't try to ask a
        // human who isn't there).
        var interactive = !parsed.PrintMode
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected;
        // Signal working/waiting through the terminal (taskbar progress + tab title), like
        // claude-code's animated CMD icon. Interactive TTY only; harmless no-op elsewhere.
        if (interactive) TerminalStatus.Enable();
        UserInputQueue? inputQueue = interactive ? new UserInputQueue() : null;
        ConsoleInput? turnInput = interactive
            ? new ConsoleInput(inputQueue!, AnsiConsole.Console, slashCatalog)
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
        // Permission mode (claude-cli's Shift+Tab cycle): Default → AcceptEdits → Plan, plus Bypass.
        // Interactive-only (Plan's ExitPlanMode + the Ask prompt need a human). Seeded from --plan /
        // --dangerously-skip-permissions / permissions.defaultMode; toggled at runtime via Shift+Tab
        // (TUI) or /mode (both drivers). PermissionModeState implements IPlanModeSwitch, so all the
        // existing plan-mode plumbing keeps working with Plan as one point on the cycle.
        static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var dm = settings.Permissions.DefaultMode;
        var initialMode =
            parsed.DangerouslySkipPermissions ? PermissionMode.Bypass
            : parsed.Plan || Eq(dm, "plan") ? PermissionMode.Plan
            : Eq(dm, "acceptEdits") ? PermissionMode.AcceptEdits
            : Eq(dm, "bypassPermissions") ? PermissionMode.Bypass
            : PermissionMode.Default;
        PermissionModeState? planMode = interactive ? new PermissionModeState(initialMode) : null;

        // Team mode (orchestrator-only, sticky): a runtime switch like plan mode, off at startup and
        // toggled by /team (on, via the wizard) and /end-team (off). Interactive only — the wizard and
        // the delegation loop need a human. Shared with AgentLoop (gating), the REPL (toggle), and the TUI.
        TeamModeState? teamMode = interactive ? new TeamModeState() : null;

        // The persistent bottom-input TUI (claude-code layout: output scrolls above, a multi-line
        // input box stays pinned and writable during and between turns). Default for an interactive
        // ANSI TTY; ZDT_NO_TUI or ZDT_BASIC_INPUT falls back to the line-based REPL. When on it
        // becomes the input source, the turn-capture hook, and the AskUserQuestion/ExitPlanMode
        // prompter; subagent activity streams into its scroll region (the fleet view is off in TUI).
        var tuiMode = interactive
            && inputQueue is not null
            && !basicInput
            && AnsiConsole.Console.Profile.Capabilities.Ansi
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZDT_NO_TUI"));
        BottomInputTui? tui = tuiMode
            ? new BottomInputTui(inputQueue!, AnsiConsole.Console, parsed.DangerouslySkipPermissions,
                slashCatalog, planMode, teamMode)
            : null;

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
        // The human-facing prompter for AskUserQuestion / ExitPlanMode: the TUI when it's on
        // (it owns the console), otherwise the ConsoleInput driver.
        Zdtllm.Tools.IInteractivePrompter? prompter = tui is not null ? tui : turnInput;
        if (prompter is not null)
            registry.Register(new AskUserQuestionTool(prompter));
        if (prompter is not null && planMode is not null)
            registry.Register(new ExitPlanModeTool(planMode, prompter));

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
        // Runtime <env> facts (cwd / OS / shell / date / git branch), computed once at startup and
        // injected into the composed system prompt. Best-effort: any probe failure omits its line.
        var envInfo = BuildEnvInfo(cwd);

        // Print mode pipes stdout through the shell so a spinner/markdown renderer would
        // mangle redirected output. Interactive mode benefits from rich rendering — EXCEPT under
        // the bottom-input TUI, which owns the screen with a scroll region and can't share it with
        // Spectre's live spinner. There the model's text is still rendered as markdown — but via
        // MarkdownRenderer.RenderToAnsi into the scroll-region writer (plain ANSI lines, no
        // renderables/spinners), so answers don't show up as raw ###/**/` noise.
        var richConsole = (parsed.PrintMode || tuiMode) ? null : AnsiConsole.Console;
        Func<string, string>? markdownAnsi = tuiMode
            ? md => Zdtllm.Core.MarkdownRenderer.RenderToAnsi(md, TerminalTextWidth())
            : null;

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
                    skills: skills,
                    envInfo: envInfo),
            },
            context: contextManager,
            richConsole: formatOwnsStdout ? null : richConsole,
            observer: observer,
            inputQueue: inputQueue,
            planMode: planMode,
            typeAhead: turnInput,
            markdownAnsi: formatOwnsStdout ? null : markdownAnsi,
            // Same human-facing prompter that backs AskUserQuestion/ExitPlanMode: drives the
            // interactive allow / always-allow / deny prompt when a tool call needs permission.
            // Null in print mode (no TTY) → the loop keeps the text-error fallback.
            prompter: prompter,
            // The TUI (when on) animates mid-turn auto-compact on its status row. Null in rich mode,
            // where AgentLoop falls back to a Spectre status spinner via the rich console instead.
            inputCapture: tui,
            // Team mode: when active, the orchestrator's mutating tools are hidden + blocked and each
            // turn is grounded with the delegation reminder (built from the live project-agent roster).
            teamMode: teamMode,
            teamAgents: teamAgents);

        // Task tool needs the parent agent to spawn subagents from. Register it AFTER the
        // agent is built — the registry holds a live reference, so the parent agent will see
        // Task on subsequent turns.
        //
        // The model resolver lets per-subagent_type tiering (litellm.subagentModels) apply
        // at dispatch time. The captured snapshot of Models / SubagentModels is fine because
        // settings are read once at startup and never mutate at runtime; if that ever changes,
        // this delegate would need to read live state instead.
        // How subagent activity is surfaced:
        //   • interactive TTY (both the bottom-input TUI and the classic REPL) → a navigable "fleet
        //     view": when ≥2 subagents run at once, a Spectre live display lists them and shows the
        //     focused agent's output, arrow-key to switch. It takes the screen over via the active
        //     input driver's IConsoleExclusive (in TUI mode that lifts the scroll region and restores
        //     it after). ZDT_NO_AGENT_VIEW disables it, falling back to the tagged stream below.
        //   • otherwise → each subagent's activity streamed tagged to stderr (off for a plain
        //     scripted -p run unless --verbose, so automated callers get a clean stderr).
        // The console owner is whichever input driver holds the terminal — the TUI when present,
        // else the REPL line editor. Single-agent / pre-view / fallback activity is echoed to the
        // fleet view's own stream: the TUI's scroll-region writer in TUI mode, else stderr.
        var viewOwner = (Zdtllm.Core.AgentFleet.IConsoleExclusive?)tui ?? turnInput;
        var agentViewEnabled = interactive
            && viewOwner is not null
            && AnsiConsole.Console.Profile.Capabilities.Ansi
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZDT_NO_AGENT_VIEW"));
        TextWriter fleetStream = tui is not null ? tui.Output : Console.Error;
        AgentFleetView? fleetView = agentViewEnabled
            ? new AgentFleetView(AnsiConsole.Console, viewOwner, fleetStream)
            : null;
        // When the fleet view is active it IS the monitor and owns all subagent output (streaming the
        // single-agent case itself), so the plain sink is null. Without it, fall back to the TUI's
        // scroll region / stderr per the scripted-run rules.
        TextWriter? subagentSink = fleetView is not null
            ? null
            : tui is not null
                ? tui.Output
                : (parsed.PrintMode && !parsed.Verbose ? null : Console.Error);
        var subagentRunner = new SubagentRunner(agent, subagentSink, fleetView, teamAgents);
        var modelAliases = settings.LiteLLM.Models;
        var subagentOverrides = settings.LiteLLM.SubagentModels;
        var smallFastModel = settings.LiteLLM.SmallFastModel;
        Func<string, string?, string?> tieredModelResolver = (subagentType, _parent) =>
        {
            // A project subagent is authoritative about its own model: an explicit model: wins, and
            // model: inherit (normalised to null by the loader) means "inherit the parent". We must
            // NOT fall through to the tiered resolver in the inherit case — a project agent named
            // e.g. "explore" would otherwise pick up the builtin light-tier default and never inherit.
            if (teamAgents.TryGet(subagentType, out var def))
                return def.Model is null ? null : SubagentModelResolver.ExpandAlias(def.Model, modelAliases);
            return SubagentModelResolver.Resolve(subagentType, modelAliases, subagentOverrides, smallFastModel);
        };
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
            // Reset the terminal FIRST (scroll region, autowrap, alt screen) — on a hard exit
            // (double Ctrl+C) the RunAsync finally never runs, and without this the user's shell
            // is left inside a stale DECSTBM region with autowrap off. Dispose is idempotent, so
            // the normal finally path doesn't double-reset.
            try { tui?.Dispose(); } catch { /* swallow */ }
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

        // In TUI mode the bottom-input TUI is the input source, the turn-capture hook (drives the
        // "thinking" indicator), and the output sink (its scroll region); Spectre rich rendering is
        // off. Otherwise the classic line REPL wiring.
        var replOutput = tui is not null ? tui.Output : Console.Out;
        var replError = tui is not null ? tui.Output : Console.Error;
        var replInputSource = tui is not null ? (IReplInputSource)tui : richInputSource;
        var replCapture = tui is not null ? (ITurnInputCapture)tui : turnInput;

        // Resumed a session (-c / -r / existing --session-id)? Replay the prior conversation so the
        // user sees the context they're continuing, before the input box takes over. No-op for a
        // fresh session. Rendered with the same markdown path the REPL uses (TUI ANSI vs richConsole).
        PrintResumedTranscript(session, replOutput, richConsole, markdownAnsi);

        // Seed ↑/↓ input history with this session's prior user turns so recall works IMMEDIATELY on
        // a resumed session — without this, history only held messages submitted in the current run,
        // so ↑ did nothing until you sent something new. Seed whichever driver is active (both are
        // harmless to seed). Oldest→newest so ↑ walks back from the most recent.
        var pastUserMessages = session.Messages
            .Where(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content!)
            .ToList();
        if (pastUserMessages.Count > 0)
        {
            tui?.SeedHistory(pastUserMessages);
            turnInput?.SeedHistory(pastUserMessages);
        }

        var repl = new Repl(
            session,
            agent,
            Console.In,
            replOutput,
            replError,
            cwd,
            richConsole: richConsole,
            subagentRunner: subagentRunner,
            inputQueue: inputQueue,
            inputCapture: replCapture,
            planMode: planMode,
            richInput: replInputSource,
            mcpStatus: () => BuildMcpStatusText(mcpManager),
            configDump: () => BuildConfigDump(settings),
            customCommands: customCommands,
            // Team mode: /team runs the define-a-subagent wizard through this same prompter (TUI or
            // console), writes it into teamAgents, and flips teamMode on; /end-team flips it off.
            teamMode: teamMode,
            teamAgents: teamAgents,
            prompter: prompter);

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
                // runtime tear us down. The TUI must be disposed HERE: with e.Cancel = false the
                // OS terminates the process directly and neither the RunAsync finally nor
                // AppDomain.ProcessExit runs — without this the user's shell would be left inside
                // a stale scroll region with autowrap off.
                repl.PrintFarewell();
                tui?.Dispose();
                TerminalStatus.Clear();
                e.Cancel = false;
                programCts.Cancel();
                return;
            }

            e.Cancel = true;
            // In TUI mode the hint must go through the scroll-region writer — a raw stderr write
            // would land at the parked cursor inside the input box and garble it.
            var hintSink = tui is not null ? tui.Output : Console.Error;
            hintSink.WriteLine("  (press Ctrl+C again to exit)");
            hintSink.Flush();
            ctrlCResetTimer.Change(2000, Timeout.Infinite);
        };

        return await repl.RunAsync(parsed.Query, programCts.Token).ConfigureAwait(false);
        }
        finally
        {
            TerminalStatus.Clear();
            tui?.Dispose();
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
    /// Render the MCP server status for the <c>/mcp</c> command: one line per server with its
    /// connected/failed state and tool count. Built here (not in Core's Repl) because the CLI owns
    /// the McpManager — Core stays free of a Zdtllm.Mcp dependency.
    /// </summary>
    /// <summary>Render the effective settings for the <c>/config</c> command, with the API key
    /// redacted. Built in the CLI (which owns the settings object) and passed to the REPL as a
    /// delegate so Core stays free of a Zdtllm.Config dependency.</summary>
    private static string BuildConfigDump(EffectiveSettings s)
    {
        const string reset = "\x1b[0m";
        const string cyan = "\x1b[38;2;27;234;205m";
        const string body = "\x1b[38;2;232;237;242m";
        const string mute = "\x1b[38;2;104;123;137m";

        static string Redact(string? key) =>
            string.IsNullOrEmpty(key) ? "(unset)" : key.Length <= 6 ? "***" : $"{key[..3]}…{key[^2..]}";
        string Row(string k, string? v) => $"  {mute}{k}:{reset} {body}{(string.IsNullOrEmpty(v) ? "(unset)" : v)}{reset}";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{cyan}effective settings{reset}");
        sb.AppendLine().Append(Row("model", s.Model));
        var l = s.LiteLLM;
        sb.AppendLine().Append(Row("litellm.baseUrl", l.BaseUrl));
        sb.AppendLine().Append(Row("litellm.apiKey", Redact(l.ApiKey)));
        sb.AppendLine().Append(Row("litellm.toolCallingMode", l.ToolCallingMode));
        sb.AppendLine().Append(Row("litellm.reasoningEffort", l.ReasoningEffort));
        sb.AppendLine().Append(Row("litellm.temperature", l.Temperature?.ToString()));
        sb.AppendLine().Append(Row("litellm.topP", l.TopP?.ToString()));
        sb.AppendLine().Append(Row("litellm.topK", l.TopK?.ToString()));
        sb.AppendLine().Append(Row("litellm.minP", l.MinP?.ToString()));
        sb.AppendLine().Append(Row("litellm.maxTokens", l.MaxTokens?.ToString()));
        sb.AppendLine().Append(Row("litellm.frequencyPenalty", l.FrequencyPenalty?.ToString()));
        sb.AppendLine().Append(Row("litellm.presencePenalty", l.PresencePenalty?.ToString()));
        sb.AppendLine().Append(Row("litellm.vision", l.Vision?.ToString()));
        sb.AppendLine().Append(Row("litellm.models", l.Models.Count == 0 ? null : string.Join(", ", l.Models.Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine().Append(Row("litellm.contextWindows", l.ContextWindows.Count == 0 ? null : string.Join(", ", l.ContextWindows.Select(kv => $"{kv.Key}={kv.Value:N0}"))));
        sb.AppendLine().Append(Row("litellm.subagentModels", l.SubagentModels.Count == 0 ? null : string.Join(", ", l.SubagentModels.Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine().Append(Row("permissions", $"allow={s.Permissions.Allow.Length} ask={s.Permissions.Ask.Length} deny={s.Permissions.Deny.Length}"));
        sb.AppendLine().Append(Row("permissions.defaultMode", s.Permissions.DefaultMode));
        sb.AppendLine().Append(Row("mcp.initTimeoutSeconds", s.Mcp.InitTimeoutSeconds?.ToString()));
        sb.AppendLine().Append(Row("env keys", s.Env.Count == 0 ? null : string.Join(", ", s.Env.Keys)));
        return sb.ToString();
    }

    private static string BuildMcpStatusText(McpManager manager)
    {
        const string reset = "\x1b[0m";
        const string cyan = "\x1b[38;2;27;234;205m";
        const string body = "\x1b[38;2;232;237;242m";
        const string mute = "\x1b[38;2;104;123;137m";
        const string red = "\x1b[38;2;229;77;77m";

        var statuses = manager.Statuses;
        if (statuses.Count == 0)
            return $"{mute}  No MCP servers configured. Pass --mcp-config <file> to connect one.{reset}";

        var sb = new System.Text.StringBuilder();
        sb.Append($"{mute}  MCP servers ({statuses.Count}):{reset}");
        foreach (var s in statuses)
        {
            sb.AppendLine();
            if (s.Connected)
                sb.Append($"  {cyan}●{reset} {body}{s.Name}{reset} {mute}— {s.ServerInfo}, {s.ToolCount} tool(s){reset}");
            else
                sb.Append($"  {red}○{reset} {body}{s.Name}{reset} {red}— failed: {s.ErrorMessage}{reset}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves which Session to run against, given the parsed flags. Honours
    /// --session-id (create-or-resume), -c (resume most recent for cwd), -r
    /// (resume by id), otherwise builds an ephemeral non-persistent session.
    /// Persistent sessions update the recent-tracker so a future -c finds them.
    /// </summary>
    /// <summary>
    /// On resume, replay the prior conversation to the terminal so the user sees the context they're
    /// continuing. Renders user prompts and assistant messages (as markdown); assistant tool-call
    /// turns collapse to a one-line "used tools" note and raw tool results are omitted to keep the
    /// transcript readable. No-op for a fresh session (no user/assistant messages yet).
    /// </summary>
    internal static void PrintResumedTranscript(
        Zdtllm.Core.Sessions.Session session,
        TextWriter output,
        Spectre.Console.IAnsiConsole? richConsole,
        Func<string, string>? markdownAnsi)
    {
        var history = session.Messages
            .Where(m => m.Role is "user" or "assistant")
            .ToList();
        if (history.Count == 0) return;

        // Raw ANSI (the transcript goes to a plain TextWriter — the TUI scroll region or stdout).
        const string reset = "\x1b[0m";
        const string cyan = "\x1b[38;2;27;234;205m";
        const string body = "\x1b[38;2;232;237;242m";
        const string mute = "\x1b[38;2;104;123;137m";

        var userTurns = history.Count(m => m.Role == "user");
        output.WriteLine();
        output.WriteLine($"{mute}──── resumed conversation · {userTurns} turn{(userTurns == 1 ? "" : "s")} ────{reset}");
        output.WriteLine();

        foreach (var m in history)
        {
            if (m.Role == "user")
            {
                // Prompt marker on the first line; continuation lines indented under it.
                var text = (m.Content ?? string.Empty).ReplaceLineEndings("\n");
                output.WriteLine($"{cyan}> {reset}{body}{text.Replace("\n", "\n  ")}{reset}");
            }
            else // assistant
            {
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    if (richConsole is not null)
                    {
                        richConsole.Write(Zdtllm.Core.MarkdownRenderer.Render(m.Content));
                        richConsole.WriteLine();
                    }
                    else if (markdownAnsi is not null)
                    {
                        output.WriteLine(markdownAnsi(m.Content));
                    }
                    else
                    {
                        output.WriteLine(m.Content);
                    }
                }
                if (!m.ToolCalls.IsDefaultOrEmpty)
                {
                    var names = string.Join(", ", m.ToolCalls.Select(t => t.FunctionName).Distinct());
                    output.WriteLine($"{mute}  ⚙ {names}{reset}");
                }
            }
        }

        output.WriteLine();
        output.WriteLine($"{mute}──── end of history · continue below ────{reset}");
        output.WriteLine();
        output.Flush();
    }

    /// <summary>Wrap width for markdown rendered into the TUI scroll region: current terminal
    /// width minus a safety column (the TUI clips at cols-1), floored for tiny windows.</summary>
    private static int TerminalTextWidth()
    {
        try { return Math.Max(40, Console.WindowWidth - 2); }
        catch { return 78; }
    }

    /// <summary>
    /// Build the runtime <c>&lt;env&gt;</c> block injected into the system prompt: working dir, OS,
    /// the Bash tool's actual shell, today's date, and the git branch. Every probe is best-effort —
    /// a failure just omits that line, never throws at startup. The shell line matters most on
    /// Windows: the Bash tool runs <c>bash -c</c> (git-bash), so telling the model to target POSIX
    /// bash stops it emitting PowerShell/cmd.
    /// </summary>
    private static string? BuildEnvInfo(string cwd)
    {
        try
        {
            var lines = new List<string>
            {
                $"Working directory: {cwd}",
                $"Platform: {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})",
                "Shell for the Bash tool: POSIX bash (runs `bash -c`) — emit bash, never PowerShell or cmd.",
                $"Today's date: {DateTime.Now:yyyy-MM-dd}",
            };
            var branch = TryGitBranch(cwd);
            if (!string.IsNullOrEmpty(branch)) lines.Add($"Git branch: {branch}");
            return string.Join("\n", lines);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve the current git branch by reading <c>.git/HEAD</c> up the directory tree — no process
    /// spawn, so it's fast and can't hang startup. Returns the branch name, a short SHA when
    /// detached, or null when not in a git repo / on any error (the env block just omits the line).
    /// </summary>
    private static string? TryGitBranch(string cwd)
    {
        try
        {
            var dir = new DirectoryInfo(cwd);
            while (dir is not null)
            {
                var head = Path.Combine(dir.FullName, ".git", "HEAD");
                if (File.Exists(head))
                {
                    var content = File.ReadAllText(head).Trim();
                    const string prefix = "ref: refs/heads/";
                    if (content.StartsWith(prefix, StringComparison.Ordinal))
                        return content[prefix.Length..];
                    return content.Length >= 7 ? content[..7] : null; // detached HEAD → short sha
                }
                dir = dir.Parent;
            }
        }
        catch { /* not a repo / unreadable → omit */ }
        return null;
    }

    private static string? TryReadMemoryFile(string cwd)
    {
        // User ~/.zdtllm/ZDTLLM.md + every ZDTLLM.md from the repo root down to cwd, each with
        // @import expansion. (Was: read only cwd/ZDTLLM.md.)
        try { return Zdtllm.Core.MemoryLoader.Load(cwd); }
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
        int? settingsWindow = null;

        var alias = parsed.Model ?? settings.Model;
        if (!string.IsNullOrEmpty(alias)
            && settings.LiteLLM.ContextWindows.TryGetValue(alias, out var fromSettings)
            && fromSettings > 0)
        {
            settingsWindow = fromSettings;
        }

        // Ask the proxy what it actually serves. Best-effort (10 s timeout, [] on failure).
        int? servedWindow = null;
        var modelInfos = await client.GetModelInfoAsync().ConfigureAwait(false);
        var match = modelInfos.FirstOrDefault(m =>
            string.Equals(m.ModelName, resolvedModel, StringComparison.Ordinal));
        if (match?.EffectiveContextWindow is int apiWindow && apiWindow > 0)
            servedWindow = apiWindow;

        int window;
        if (settingsWindow is int sw)
        {
            window = sw;
            // Clamp + warn when the configured window materially exceeds what the route serves.
            // Common failure: contextWindows=900000 on a hosted GLM/vLLM route that only serves
            // ~131k — thresholds compute off 900k so auto-compaction fires at ~810k while the proxy
            // already 400s near 131k, effectively disabling compaction. Clamp to the served size.
            if (servedWindow is int served && sw > served + served / 10)
            {
                await Console.Error.WriteLineAsync(
                    $"zdt: litellm.contextWindows for '{resolvedModel}' is {sw:N0} but the route serves only " +
                    $"~{served:N0} (max_input_tokens); clamping to {served:N0} so auto-compaction fires before " +
                    "the proxy rejects the request. Set contextWindows to the served size to silence this.")
                    .ConfigureAwait(false);
                window = served;
            }
        }
        else if (servedWindow is int served2)
        {
            window = served2;
        }
        else
        {
            // Default to 200k when neither settings nor /model/info provide a value. Most modern
            // frontier-class models ship 128k+ contexts, so 200k keeps ContextManager active by
            // default. Smaller-window deployments (vLLM --max-model-len 16384) MUST set
            // litellm.contextWindows.<alias> — otherwise auto-compact won't fire before a 400.
            window = 200_000;
        }

        var mediumName = settings.LiteLLM.Models.TryGetValue("medium", out var m) && !string.IsNullOrEmpty(m)
            ? m
            : resolvedModel;

        return new ContextManager(window, mediumName);
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
        // neither is set, infer from the model name via ModelHeuristics: open-weights chat
        // templates (deepseek / hermes / kimi / yi / mistral-nemo / local) generally don't
        // expose OpenAI-shaped function-calling on LiteLLM and fall back to text — Native mode would
        // silently drop tool calls every turn. Auto-switching to XML + a one-line stderr note keeps
        // them working out of the box. GLM and Qwen are deliberately NOT in that set: both serve
        // native tool_calls through a modern OpenAI-compatible endpoint (vLLM glm47/glm45 for GLM;
        // llama.cpp --jinja / vLLM hermes parser for Qwen — verified live against a Qwen3.6-A3B route),
        // so they default to native; a raw-passthrough endpoint with no server-side tool parser sets
        // toolCallingMode=xml explicitly.
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
    /// <c>deepseek/deepseek-r1</c> or <c>mistral-nemo-12b</c> still trigger. GLM and Qwen are
    /// excluded — they serve native tool_calls on a modern runtime (see ModelHeuristics).
    /// Wrong matches just push a model that supports both modes onto XML — slightly more
    /// verbose tool calls, no functional regression. Missed matches leave the existing
    /// "explicit-or-native" behaviour, which is the conservative default.
    /// </summary>
    // Thin CLI-side wrapper over the shared heuristic (kept for the internal test surface); the
    // real logic — and its GLM=native rationale — lives in Zdtllm.Core.ModelHeuristics so the setup
    // wizard uses the exact same predicate.
    internal static bool LooksLikeXmlOnlyModel(string modelName) =>
        Zdtllm.Core.ModelHeuristics.LooksLikeXmlOnly(modelName);

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
