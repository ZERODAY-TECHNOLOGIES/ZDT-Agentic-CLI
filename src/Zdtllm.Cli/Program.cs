using System.Reflection;
using Spectre.Console;
using Zdtllm.Config;
using Zdtllm.Core;
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

    private static async Task<int> RunAsync(string[] args)
    {
        var parsed = ParseArgs(args);

        if (parsed.ShowVersion) { PrintVersion(); return 0; }
        if (parsed.ShowHelp) { PrintHelp(); return 0; }

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

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(settings.LiteLLM.TimeoutSeconds ?? 120),
        };
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
            richConsole: richConsole);

        // Task tool needs the parent agent to spawn subagents from. Register it AFTER the
        // agent is built — the registry holds a live reference, so the parent agent will see
        // Task on subsequent turns.
        var subagentRunner = new SubagentRunner(agent);
        registry.Register(new TaskTool(subagentRunner));

        // Single CTS feeds program-wide cancellation: a second Ctrl+C exits the process,
        // a first one just halts the current turn (handled inside the REPL via per-turn CTS
        // linked to this one). Print mode short-circuits to "first Ctrl+C exits".
        using var programCts = new CancellationTokenSource();

        // Ensure MCP server subprocesses are cleaned up no matter how we exit (normal,
        // exception, Ctrl+C). Wrapping the rest of RunAsync in try/finally is the simplest
        // way to guarantee disposal across both print and interactive paths.
        try
        {

        if (parsed.PrintMode)
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; programCts.Cancel(); };
            await agent.RunTurnAsync(
                session,
                parsed.Query!,
                output: Console.Out,
                status: Console.Error,
                ct: programCts.Token).ConfigureAwait(false);
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
            // Reset the counter once the current turn finishes — we install a one-shot timer
            // via Task.Delay so a slow second Ctrl+C doesn't accidentally exit.
            _ = Task.Delay(1500).ContinueWith(_ => Interlocked.Exchange(ref ctrlCCount, 0));
        };

        return await repl.RunAsync(parsed.Query, programCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await mcpManager.DisposeAsync().ConfigureAwait(false);
        }
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

        if (window is null) return null;

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
        Console.WriteLine("  --session-id <uuid>            create or resume a persistent session at this id");
        Console.WriteLine("  -c, --continue                 resume the most recent session for this directory");
        Console.WriteLine("  -r, --resume <uuid>            resume the specified session");
        Console.WriteLine("  --version                      print version and exit");
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
        public bool ShowVersion { get; set; }
        public bool ShowHelp { get; set; }
        public string? Query { get; set; }
    }
}
