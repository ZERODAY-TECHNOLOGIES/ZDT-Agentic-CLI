using Zdtllm.Core.Sessions;

namespace Zdtllm.Core.Repl;

/// <summary>
/// Interactive REPL loop. Reads single-line input from the given TextReader,
/// dispatches slash commands or runs a session turn, repeats until /exit or EOF.
/// All I/O goes through the supplied readers/writers so this class is unit-testable
/// without touching System.Console.
/// </summary>
public sealed class Repl
{
    private readonly Session _session;
    private readonly AgentLoop _agent;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly string _cwd;
    private readonly ReplOptions _options;

    public Repl(
        Session session,
        AgentLoop agent,
        TextReader input,
        TextWriter output,
        TextWriter error,
        string cwd,
        ReplOptions? options = null)
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
    }

    public async Task<int> RunAsync(string? initialPrompt = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(initialPrompt))
        {
            await ProcessUserTurnAsync(initialPrompt, ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            await _output.WriteAsync(Palette.Cyan("> ")).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);

            string? line;
            try { line = await _input.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return 0; }

            if (line is null) // EOF
            {
                await _output.WriteLineAsync().ConfigureAwait(false);
                return 0;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('/'))
            {
                var slashResult = await HandleSlashAsync(trimmed, ct).ConfigureAwait(false);
                if (slashResult == SlashOutcome.Exit) return 0;
                continue;
            }

            await ProcessUserTurnAsync(trimmed, ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task ProcessUserTurnAsync(string prompt, CancellationToken ct)
    {
        // Mid-turn auto-compact (hard threshold) is now AgentLoop's responsibility — it
        // fires between iterations regardless of whether we're driving the parent here
        // or a subagent inside SubagentRunner. We keep ONLY the post-turn soft-threshold
        // hint here because it's a UI nudge specific to the interactive REPL.
        try
        {
            await _agent.RunTurnAsync(_session, prompt, _output, _error, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _error.WriteLineAsync(Palette.Mute("(turn cancelled)")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync(Palette.Red($"zdt: {ex.Message}")).ConfigureAwait(false);
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

            case "/permissions":
                await PrintPermissionsAsync().ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/compact":
                await HandleCompactCommandAsync(ct).ConfigureAwait(false);
                return SlashOutcome.Continue;

            case "/context":
                await HandleContextCommandAsync().ConfigureAwait(false);
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
        await WriteCommandRowAsync("/permissions", "show the current permission rule set").ConfigureAwait(false);
        await WriteCommandRowAsync("/init", "create ZDTLLM.md (project memory file) in the cwd").ConfigureAwait(false);
        await WriteCommandRowAsync("/compact", "summarize older turns to free context").ConfigureAwait(false);
    }

    private Task WriteCommandRowAsync(string command, string description) =>
        _output.WriteLineAsync($"  {Palette.Cyan(command),-26} {Palette.Mute(description)}");

    private async Task PrintStatusAsync()
    {
        await _output.WriteLineAsync($"  {Palette.Mute("session:")} {Palette.Body(SessionDisplay())}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  {Palette.Mute("model:")} {Palette.GoldBold(_session.Model)}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  {Palette.Mute("mode:")} {Palette.Body(_session.Mode.ToString().ToLowerInvariant())}").ConfigureAwait(false);
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

    private async Task PrintPermissionsAsync()
    {
        var rs = _agent.Permissions;
        var counts = rs.RuleCounts;
        await _output.WriteLineAsync(
                $"  {Palette.Mute("rules:")} " +
                $"{Palette.Red($"deny={counts.deny}")} " +
                $"{Palette.Gold($"ask={counts.ask}")} " +
                $"{Palette.Cyan($"allow={counts.allow}")}")
            .ConfigureAwait(false);
        await _output.WriteLineAsync(
                Palette.Mute("  Defaults: tools requiring permission (Bash, Edit, Write, WebFetch, WebSearch, Skill) Ask without an explicit allow."))
            .ConfigureAwait(false);
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
