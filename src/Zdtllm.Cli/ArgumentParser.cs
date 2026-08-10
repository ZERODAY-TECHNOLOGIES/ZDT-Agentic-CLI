namespace Zdtllm.Cli;

/// <summary>
/// Parses zdt's argv into a strongly-typed bag. Extracted from Program.cs so it can be
/// unit-tested directly via the InternalsVisibleTo to Zdtllm.Core.Tests configured on
/// Cli.csproj. Pure function: no I/O, no globals — just argv → ParsedArgs.
/// </summary>
internal static class ArgumentParser
{
    public static ParsedArgs Parse(string[] args)
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
                case "--incognito":
                case "--private":
                    result.Incognito = true;
                    break;
                case "--bare":
                    result.Bare = true;
                    break;
                case "--plan":
                    result.Plan = true;
                    break;
                case "--workflow":
                    result.Workflow = NextValue(args, ref i, "--workflow");
                    break;
                case "--arg":
                    result.WorkflowArgs.Add(NextValue(args, ref i, "--arg"));
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
                case "--mcp-init-timeout-seconds":
                    result.McpInitTimeoutSeconds = int.Parse(NextValue(args, ref i, "--mcp-init-timeout-seconds"));
                    break;
                case "--require-mcp":
                    result.RequireMcp = true;
                    break;
                case "--verbose":
                    result.Verbose = true;
                    break;
                case "--output-format":
                    result.OutputFormat = NextValue(args, ref i, "--output-format");
                    break;
                case "--tools":
                case "--allowed-tools":
                    // Anthropic-compatible: positional list of tool names until the next flag.
                    // Each token may itself be comma-separated (legacy zdt form). Both flag
                    // names are aliases — claude uses --allowed-tools, zdt historically used
                    // --tools, AppSec-Automator uses both depending on the call site.
                    foreach (var token in NextMultiValue(args, ref i, args[i]))
                        foreach (var name in token.Split(',',
                                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            result.AllowedTools.Add(name);
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
                    // Anthropic-compat: --resume takes an OPTIONAL session id. With an id it
                    // resumes that session directly; with no id (next token is another flag or
                    // absent) it launches the interactive picker of recent conversations.
                    var resumeId = TryNextValue(args, ref i);
                    if (resumeId is null) result.ResumePicker = true;
                    else result.Resume = resumeId;
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

    /// <summary>
    /// Consume the next token as a value ONLY if it exists and doesn't look like a flag.
    /// Returns null (leaving the index untouched) otherwise. Used by flags whose value is
    /// optional — notably <c>--resume</c>, which falls back to an interactive picker when
    /// no session id is supplied.
    /// </summary>
    private static string? TryNextValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length || LooksLikeFlag(args[i + 1])) return null;
        return args[++i];
    }

    /// <summary>
    /// Multi-value flag: consumes every subsequent token until the next flag-looking arg.
    /// Stops on tokens beginning with "--" (long flag) or "-X" where X is a letter (short
    /// flag). Negative numbers like "-1.5" pass through as values.
    /// Examples for --tools: "Read Glob Grep", "Read,Glob,Grep", "mcp__a__b mcp__a__c Read".
    /// </summary>
    private static List<string> NextMultiValue(string[] args, ref int i, string flag)
    {
        var values = new List<string>();
        while (i + 1 < args.Length && !LooksLikeFlag(args[i + 1]))
            values.Add(args[++i]);
        if (values.Count == 0)
            throw new ArgumentException($"{flag} requires at least one value.");
        return values;
    }

    private static bool LooksLikeFlag(string token)
    {
        if (token.Length < 2 || token[0] != '-') return false;
        if (token[1] == '-') return true;
        return char.IsLetter(token[1]);
    }
}

internal sealed class ParsedArgs
{
    public bool PrintMode { get; set; }
    public string? Model { get; set; }
    public int? MaxTurns { get; set; }
    public int? MaxParallel { get; set; }
    public bool DangerouslySkipPermissions { get; set; }
    public bool NoWizard { get; set; }

    /// <summary>Incognito: run a purely in-memory session — nothing is written to the sessions dir, and
    /// (interactively via <c>/incognito</c>) an already-persisted file is deleted. Tool side-effects on
    /// the workspace still happen; only the conversation record is ephemeral.</summary>
    public bool Incognito { get; set; }
    public bool Bare { get; set; }
    /// <summary>Start the interactive session in plan mode (read-only; drafts a plan for approval).</summary>
    public bool Plan { get; set; }
    /// <summary>Name of a declarative workflow (from .zdtllm/workflows/) to run one-shot, then exit.</summary>
    public string? Workflow { get; set; }
    /// <summary>Repeatable <c>key=value</c> inputs for the workflow (list values are comma-separated).</summary>
    public List<string> WorkflowArgs { get; } = new();
    public string? ToolCallingMode { get; set; }
    public string? SessionId { get; set; }
    public bool Continue { get; set; }
    public string? Resume { get; set; }
    /// <summary>
    /// Set when <c>-r</c>/<c>--resume</c> was passed WITHOUT a session id. Triggers the
    /// interactive picker (arrow-key list of recent conversations) in interactive mode.
    /// Mutually exclusive with <see cref="Resume"/> being non-null.
    /// </summary>
    public bool ResumePicker { get; set; }
    public string? SystemPrompt { get; set; }
    public string? SystemPromptFile { get; set; }
    public string? AppendSystemPrompt { get; set; }
    public string? AppendSystemPromptFile { get; set; }
    public List<string> AddDirs { get; } = new();
    public List<string> McpConfigs { get; } = new();
    /// <summary>
    /// Per-server MCP initialise/handshake timeout. Wins over <c>mcp.initTimeoutSeconds</c>
    /// in settings.json; both fall back to 15 s when neither is set. Slow-booting servers
    /// (Laravel/Django on Windows + Herd, cold caches, DB-dependent auth) routinely need 30–60 s.
    /// </summary>
    public int? McpInitTimeoutSeconds { get; set; }
    /// <summary>
    /// When set, zdt exits non-zero if any MCP server in the merged <c>--mcp-config</c> set
    /// fails to boot. Off by default so a misbehaving server doesn't block the rest of the
    /// agent (the historical behaviour); flip on in CI / production runs that depend on
    /// MCP-provided tools.
    ///
    /// <para>
    /// <b>Scope:</b> the check is over the <i>declared</i> servers — i.e. those parsed from
    /// <c>--mcp-config</c>. It is therefore a no-op when no <c>--mcp-config</c> was passed
    /// at all (no servers were declared, so by definition none failed). Use it as
    /// "if I declared MCP servers, none of them is allowed to fail," not as
    /// "ensure MCP is configured at all" — for the latter the caller should validate its
    /// own argv.
    /// </para>
    /// </summary>
    public bool RequireMcp { get; set; }
    public bool Verbose { get; set; }
    public string? OutputFormat { get; set; }
    public List<string> AllowedTools { get; } = new();
    public bool ShowVersion { get; set; }
    public bool ShowHelp { get; set; }
    public bool CheckUpdates { get; set; }
    public bool SelfUpdate { get; set; }
    public string? Query { get; set; }
}
