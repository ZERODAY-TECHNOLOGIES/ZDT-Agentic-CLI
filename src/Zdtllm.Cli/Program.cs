using System.Reflection;
using Spectre.Console;
using Zdtllm.Config;
using Zdtllm.Core;
using Zdtllm.Core.Observers;
using Zdtllm.Core.Repl;
using Zdtllm.Core.Sessions;
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
        var parsed = ParseArgs(args);

        if (parsed.ShowVersion) { PrintVersion(); return 0; }
        if (parsed.ShowHelp) { PrintHelp(); return 0; }

        // Both update flags short-circuit before settings/wizard — they don't touch LiteLLM
        // and shouldn't fail just because the user hasn't configured the proxy yet.
        if (parsed.CheckUpdates) return await SelfUpdate.RunCheckUpdatesAsync().ConfigureAwait(false);
        if (parsed.SelfUpdate)   return await SelfUpdate.RunSelfUpdateAsync().ConfigureAwait(false);

        if (parsed.PrintMode && string.IsNullOrWhiteSpace(parsed.Query))
        {
            await Console.Error.WriteLineAsync("zdt -p requires a query.").ConfigureAwait(false);
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

        var registry = new ToolRegistry();
        registry.Register(new ReadTool());
        registry.Register(new WriteTool());
        registry.Register(new EditTool());
        registry.Register(new BashTool(cwd));
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new TodoWriteTool());
        registry.Register(new WebFetchTool(fetchHttp));
        registry.Register(new WebSearchTool());
        if (skills.Count > 0)
            registry.Register(new SkillTool(skills));

        // MCP servers — parse every --mcp-config in order (later entries override earlier ones
        // for the same server name), spawn each as a stdio subprocess, register its tools as
        // mcp__<server>__<tool>, and keep the manager around so we can DisposeAsync on shutdown.
        var mcpManager = new McpManager(diagnostics: Console.Error);
        await BootMcpServersAsync(parsed.McpConfigs, registry, mcpManager).ConfigureAwait(false);

        var sessionsDir = Path.Combine(cwd, ".zdtllm", "sessions");
        var recent = RecentTracker.ForUserHome();

        using var session = ResolveSession(parsed, settings, sessionsDir, recent, cwd, defaultPersistent: !parsed.PrintMode);

        var contextManager = await BuildContextManagerAsync(parsed, settings, client, session.Model)
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
                MaxTurns = parsed.MaxTurns ?? 30,
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
            observer: observer);

        // Task tool needs the parent agent to spawn subagents from. Register it AFTER the
        // agent is built — the registry holds a live reference, so the parent agent will see
        // Task on subsequent turns.
        var subagentRunner = new SubagentRunner(agent);
        registry.Register(new TaskTool(subagentRunner));

        // --tools filter: applied last so it can drop builtins, MCP tools, and Task uniformly.
        if (parsed.AllowedTools.Count > 0)
        {
            ApplyToolsAllowlist(registry, parsed.AllowedTools);
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

        if (parsed.PrintMode)
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; programCts.Cancel(); };
            // For json / stream-json the model's text goes through the observer; AgentLoop's
            // own output writer must be muted so the same text doesn't double-print to stdout.
            var loopOutput = formatOwnsStdout ? TextWriter.Null : Console.Out;
            await agent.RunTurnAsync(
                session,
                parsed.Query!,
                output: loopOutput,
                status: Console.Error,
                ct: programCts.Token).ConfigureAwait(false);

            if (aggregator is not null)
                await aggregator.EmitAsync(Console.Out, programCts.Token).ConfigureAwait(false);
            return 0;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var sessionDisplay = session.IsPersistent ? session.Id : $"{session.Id} (ephemeral)";
        Branding.PrintStartupBanner(AnsiConsole.Console, version, session.Model, session.Mode, sessionDisplay);

        var repl = new Repl(
            session,
            agent,
            Console.In,
            Console.Out,
            Console.Error,
            cwd,
            richConsole: richConsole,
            subagentRunner: subagentRunner);

        var ctrlCCount = 0;
        // Single reusable timer instead of allocating a new Task.Delay+ContinueWith on every
        // Ctrl+C. A user mashing Ctrl+C couldn't accumulate orphaned tasks anymore — the
        // timer just resets its "due time" each press.
        using var ctrlCResetTimer = new System.Threading.Timer(
            _ => Interlocked.Exchange(ref ctrlCCount, 0), null, Timeout.Infinite, Timeout.Infinite);
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C: cancel the active turn (kills agent + every subagent it spawned via
            // the linked CT chain) but keep the REPL alive. Second Ctrl+C in a row: exit hard.
            if (Interlocked.Increment(ref ctrlCCount) >= 2)
            {
                e.Cancel = false;
                programCts.Cancel();
                return;
            }
            e.Cancel = true;
            repl.CancelCurrentTurn();
            // (Re)arm the reset timer for 1.5 s out so a slow second Ctrl+C doesn't accidentally
            // exit. Re-Change just shifts the due time — no new allocation.
            ctrlCResetTimer.Change(1500, Timeout.Infinite);
        };

        return await repl.RunAsync(parsed.Query, programCts.Token).ConfigureAwait(false);
        }
        finally
        {
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
    /// allowlist mentions a tool that isn't registered.
    /// </summary>
    private static void ApplyToolsAllowlist(ToolRegistry registry, IReadOnlyList<string> allowed)
    {
        var keep = new HashSet<string>(allowed, StringComparer.Ordinal);
        var present = registry.All.Select(t => t.Schema.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var requested in allowed)
        {
            if (!present.Contains(requested))
                Console.Error.WriteLine($"zdt: --tools: '{requested}' is not a registered tool (typo? case mismatch?).");
        }

        var toRemove = present.Where(n => !keep.Contains(n)).ToList();
        foreach (var name in toRemove) registry.Remove(name);

        if (toRemove.Count > 0)
            Console.Error.WriteLine($"zdt: --tools allowlist active — kept {registry.All.Length}, dropped {toRemove.Count}.");
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
    }

    /// <summary>
    /// Parse every --mcp-config file, merge them in flag order, spawn the servers, and register
    /// their tools. Errors are reported per-server to stderr but never fatal — a misbehaving
    /// MCP server should not block the rest of the agent from starting.
    /// </summary>
    private static async Task BootMcpServersAsync(
        IReadOnlyList<string> configPaths,
        ToolRegistry registry,
        McpManager manager)
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
            handshakeTimeout: TimeSpan.FromSeconds(15),
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

    private static (string Model, ToolCallingMode Mode) ResolveModelAndMode(
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

        var mode = ToolCallingModeParse.FromString(
            parsed.ToolCallingMode ?? settings.LiteLLM.ToolCallingMode,
            fallback: ToolCallingMode.Native);

        return (modelName, mode);
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        var result = new ParsedArgs();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p":
                case "--print":
                    result.PrintMode = true;
                    break;
                case "--model":
                    result.Model = NextValue(args, ref i, "--model");
                    break;
                case "--max-turns":
                    result.MaxTurns = int.Parse(NextValue(args, ref i, "--max-turns"));
                    break;
                case "--max-parallel":
                    result.MaxParallel = int.Parse(NextValue(args, ref i, "--max-parallel"));
                    break;
                case "--dangerously-skip-permissions":
                    result.DangerouslySkipPermissions = true;
                    break;
                case "--no-wizard":
                    result.NoWizard = true;
                    break;
                case "--bare":
                    result.Bare = true;
                    break;
                case "--tool-calling":
                    result.ToolCallingMode = NextValue(args, ref i, "--tool-calling");
                    break;
                case "--system-prompt":
                    result.SystemPrompt = NextValue(args, ref i, "--system-prompt");
                    break;
                case "--system-prompt-file":
                    result.SystemPromptFile = NextValue(args, ref i, "--system-prompt-file");
                    break;
                case "--append-system-prompt":
                    result.AppendSystemPrompt = NextValue(args, ref i, "--append-system-prompt");
                    break;
                case "--append-system-prompt-file":
                    result.AppendSystemPromptFile = NextValue(args, ref i, "--append-system-prompt-file");
                    break;
                case "--add-dir":
                    result.AddDirs.Add(NextValue(args, ref i, "--add-dir"));
                    break;
                case "--mcp-config":
                    result.McpConfigs.Add(NextValue(args, ref i, "--mcp-config"));
                    break;
                case "--verbose":
                    result.Verbose = true;
                    break;
                case "--output-format":
                    result.OutputFormat = NextValue(args, ref i, "--output-format");
                    break;
                case "--tools":
                    foreach (var t in NextValue(args, ref i, "--tools").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        result.AllowedTools.Add(t);
                    break;
                case "--session-id":
                    result.SessionId = NextValue(args, ref i, "--session-id");
                    break;
                case "-c":
                case "--continue":
                    result.Continue = true;
                    break;
                case "-r":
                case "--resume":
                    result.Resume = NextValue(args, ref i, "--resume");
                    break;
                case "--version":
                    result.ShowVersion = true;
                    break;
                case "--check-updates":
                    result.CheckUpdates = true;
                    break;
                case "--self-update":
                    result.SelfUpdate = true;
                    break;
                case "-h":
                case "--help":
                    result.ShowHelp = true;
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        result.Query = positional.Count > 0 ? string.Join(' ', positional) : null;
        return result;
    }

    private static string NextValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"{flag} requires a value.");
        return args[++i];
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
        Console.WriteLine("  zdt -r <uuid>                  interactive, resume the given session");
        Console.WriteLine();
        Console.WriteLine("FLAGS:");
        Console.WriteLine("  -p, --print                    print mode (one-shot, exit)");
        Console.WriteLine("  --model <alias|name>           model alias (light/medium/heavy) or full name");
        Console.WriteLine("  --max-turns <n>                cap agent loop iterations (default 30)");
        Console.WriteLine("  --max-parallel <n>             cap concurrent tool calls in a parallel batch (0 = unlimited)");
        Console.WriteLine("  --dangerously-skip-permissions auto-allow tools that would otherwise prompt");
        Console.WriteLine("  --no-wizard                    skip the first-run setup wizard");
        Console.WriteLine("  --bare                         skip auto-discovery of skills");
        Console.WriteLine("  --tool-calling <native|xml>    transport for tool calls (default: native)");
        Console.WriteLine("  --system-prompt <text>         replace the default system prompt with <text>");
        Console.WriteLine("  --system-prompt-file <path>    replace the default system prompt with file contents");
        Console.WriteLine("  --append-system-prompt <text>  append <text> after the default/replaced prompt");
        Console.WriteLine("  --append-system-prompt-file <p>  append file contents after the default/replaced prompt");
        Console.WriteLine("  --add-dir <path>               add an extra accessible directory (repeatable)");
        Console.WriteLine("  --mcp-config <path>            load MCP server config from a JSON file (repeatable, last wins per server)");
        Console.WriteLine("  --verbose                      trace tool calls + results to stderr (durations, args/preview)");
        Console.WriteLine("  --output-format <fmt>          text (default) | json | stream-json — only honoured with -p");
        Console.WriteLine("  --tools <a,b,c>                allowlist of tool names — registry is filtered after MCP/Task register");
        Console.WriteLine("  --session-id <uuid>            create or resume a persistent session at this id");
        Console.WriteLine("  -c, --continue                 resume the most recent session for this directory");
        Console.WriteLine("  -r, --resume <uuid>            resume the specified session");
        Console.WriteLine("  --version                      print version and exit");
        Console.WriteLine("  --check-updates                check GitHub for a newer release and exit");
        Console.WriteLine("  --self-update                  download + install the latest release in place");
        Console.WriteLine("  -h, --help                     show this help");
        Console.WriteLine();
        Console.WriteLine($"More at {Url}");
    }

    private sealed class ParsedArgs
    {
        public bool PrintMode { get; set; }
        public string? Model { get; set; }
        public int? MaxTurns { get; set; }
        public int? MaxParallel { get; set; }
        public bool DangerouslySkipPermissions { get; set; }
        public bool NoWizard { get; set; }
        public bool Bare { get; set; }
        public string? ToolCallingMode { get; set; }
        public string? SessionId { get; set; }
        public bool Continue { get; set; }
        public string? Resume { get; set; }
        public string? SystemPrompt { get; set; }
        public string? SystemPromptFile { get; set; }
        public string? AppendSystemPrompt { get; set; }
        public string? AppendSystemPromptFile { get; set; }
        public List<string> AddDirs { get; } = new();
        public List<string> McpConfigs { get; } = new();
        public bool Verbose { get; set; }
        public string? OutputFormat { get; set; }
        public List<string> AllowedTools { get; } = new();
        public bool ShowVersion { get; set; }
        public bool ShowHelp { get; set; }
        public bool CheckUpdates { get; set; }
        public bool SelfUpdate { get; set; }
        public string? Query { get; set; }
    }
}
