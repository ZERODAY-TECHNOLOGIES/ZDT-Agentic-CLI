using System.Reflection;
using Zdtllm.Config;
using Zdtllm.Core;
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

        if (parsed.ShowVersion)
        {
            PrintVersion();
            return 0;
        }
        if (parsed.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (!parsed.PrintMode)
        {
            await Console.Error.WriteLineAsync(
                "Phase 1 only supports print mode. Run: zdt -p \"<query>\"").ConfigureAwait(false);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(parsed.Query))
        {
            await Console.Error.WriteLineAsync("zdt -p requires a query.").ConfigureAwait(false);
            return 2;
        }

        var cwd = Directory.GetCurrentDirectory();
        var settings = SettingsLoader.LoadEffectiveSettings(cwd);

        var modelAlias = parsed.Model ?? settings.Model;
        if (string.IsNullOrEmpty(modelAlias))
            throw new InvalidOperationException(
                "No model specified. Pass --model or set 'model' in .zdtllm/settings.json.");

        var modelName = settings.LiteLLM.Models.TryGetValue(modelAlias, out var resolved)
            ? resolved
            : modelAlias;

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

        var toolCallingMode = ToolCallingModeParse.FromString(
            parsed.ToolCallingMode ?? settings.LiteLLM.ToolCallingMode,
            fallback: ToolCallingMode.Native);

        var agent = new AgentLoop(client, registry, perms, new AgentLoopOptions
        {
            Model = modelName,
            MaxTurns = parsed.MaxTurns ?? 30,
            SkipPermissions = parsed.DangerouslySkipPermissions,
            ToolCallingMode = toolCallingMode,
        });

        await agent.RunOneShotAsync(
            parsed.Query!,
            output: Console.Out,
            status: Console.Error,
            ct: CancellationToken.None).ConfigureAwait(false);

        return 0;
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
        Console.WriteLine("  zdt -p \"<query>\"              run a one-shot query and exit");
        Console.WriteLine();
        Console.WriteLine("FLAGS (Phase 1):");
        Console.WriteLine("  -p, --print                    print mode (one-shot, exit)");
        Console.WriteLine("  --model <alias|name>           model alias (light/medium/heavy) or full name");
        Console.WriteLine("  --max-turns <n>                cap agent loop iterations (default 30)");
        Console.WriteLine("  --dangerously-skip-permissions auto-allow tools that would otherwise prompt");
        Console.WriteLine("  --tool-calling <native|xml>    transport for tool calls (default: native)");
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
        public bool ShowVersion { get; set; }
        public bool ShowHelp { get; set; }
        public string? Query { get; set; }
    }
}
