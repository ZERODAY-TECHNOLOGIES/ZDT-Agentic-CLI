using System.Reflection;
using Zdtllm.Config;
using Zdtllm.Core;
using Zdtllm.Core.Repl;
using Zdtllm.Core.Sessions;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
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
            throw new InvalidOperationException(
                "litellm.baseUrl is not configured. Set it in .zdtllm/settings.json.");

        if (string.IsNullOrEmpty(settings.LiteLLM.ApiKey))
            throw new InvalidOperationException(
                "litellm.apiKey is not configured. Set it in .zdtllm/settings.json " +
                "(use \"${ZDTLLM_API_KEY}\" to read from the environment).");

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

        var registry = new ToolRegistry();
        registry.Register(new ReadTool());
        registry.Register(new BashTool(cwd));

        var sessionsDir = Path.Combine(cwd, ".zdtllm", "sessions");
        var recent = RecentTracker.ForUserHome();

        using var session = ResolveSession(parsed, settings, sessionsDir, recent, cwd, defaultPersistent: !parsed.PrintMode);

        var agent = new AgentLoop(client, registry, perms, new AgentLoopOptions
        {
            Model = session.Model,
            MaxTurns = parsed.MaxTurns ?? 30,
            SkipPermissions = parsed.DangerouslySkipPermissions,
            ToolCallingMode = session.Mode,
        });

        if (parsed.PrintMode)
        {
            await agent.RunTurnAsync(
                session,
                parsed.Query!,
                output: Console.Out,
                status: Console.Error,
                ct: CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        var repl = new Repl(
            session,
            agent,
            Console.In,
            Console.Out,
            Console.Error,
            cwd);
        return await repl.RunAsync(parsed.Query, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves which Session to run against, given the parsed flags. Honours
    /// --session-id (create-or-resume), -c (resume most recent for cwd), -r
    /// (resume by id), otherwise builds an ephemeral non-persistent session.
    /// Persistent sessions update the recent-tracker so a future -c finds them.
    /// </summary>
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
                case "--dangerously-skip-permissions":
                    result.DangerouslySkipPermissions = true;
                    break;
                case "--tool-calling":
                    result.ToolCallingMode = NextValue(args, ref i, "--tool-calling");
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
        Console.WriteLine($"zdtllmcli {version}  ({Url})");
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
        Console.WriteLine("  --dangerously-skip-permissions auto-allow tools that would otherwise prompt");
        Console.WriteLine("  --tool-calling <native|xml>    transport for tool calls (default: native)");
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
        public bool DangerouslySkipPermissions { get; set; }
        public string? ToolCallingMode { get; set; }
        public string? SessionId { get; set; }
        public bool Continue { get; set; }
        public string? Resume { get; set; }
        public bool ShowVersion { get; set; }
        public bool ShowHelp { get; set; }
        public string? Query { get; set; }
    }
}
