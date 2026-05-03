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
            await _output.WriteAsync("> ").ConfigureAwait(false);
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
        var ctx = _agent.Context;
        if (ctx is not null && ctx.IsBeyondHardThreshold)
        {
            await _error.WriteLineAsync(
                $"[auto-compact at {ctx.UsagePercent}% — summarizing older turns to free context]")
                .ConfigureAwait(false);
            try
            {
                await ctx.CompactAsync(_session, _agent.Client, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _error.WriteLineAsync($"zdt: auto-compact failed: {ex.Message}")
                    .ConfigureAwait(false);
            }
        }

        try
        {
            await _agent.RunTurnAsync(_session, prompt, _output, _error, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _error.WriteLineAsync("(turn cancelled)").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync($"zdt: {ex.Message}").ConfigureAwait(false);
        }

        if (ctx is not null && ctx.IsBeyondSoftThreshold && !ctx.IsBeyondHardThreshold)
        {
            await _error.WriteLineAsync(
                $"[context at {ctx.UsagePercent}% — /compact recommended]")
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
                await _output.WriteLineAsync("Conversation history cleared (system prompt kept).")
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
                        $"Unknown command: {cmd}. Type /help for the available commands.")
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
        await _output.WriteLineAsync("Available commands:").ConfigureAwait(false);
        await _output.WriteLineAsync("  /help            show this list").ConfigureAwait(false);
        await _output.WriteLineAsync("  /exit, /quit     leave the REPL").ConfigureAwait(false);
        await _output.WriteLineAsync("  /clear           drop conversation history (system prompt kept)").ConfigureAwait(false);
        await _output.WriteLineAsync("  /status          show session id, model, mode, message count").ConfigureAwait(false);
        await _output.WriteLineAsync("  /context         show current context-window usage and per-role breakdown").ConfigureAwait(false);
        await _output.WriteLineAsync("  /model <name>    switch model used by the next turn").ConfigureAwait(false);
        await _output.WriteLineAsync("  /permissions     show the current permission rule set").ConfigureAwait(false);
        await _output.WriteLineAsync("  /init            create ZDTLLM.md (project memory file) in the cwd").ConfigureAwait(false);
        await _output.WriteLineAsync("  /compact         summarize older turns to free context").ConfigureAwait(false);
    }

    private async Task PrintStatusAsync()
    {
        await _output.WriteLineAsync($"  session: {SessionDisplay()}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  model: {_session.Model}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  mode: {_session.Mode.ToString().ToLowerInvariant()}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  messages: {_session.Messages.Count}").ConfigureAwait(false);
        await _output.WriteLineAsync($"  cwd: {_cwd}").ConfigureAwait(false);

        var ctx = _agent.Context;
        if (ctx is not null)
        {
            await _output.WriteLineAsync(
                $"  context: {ctx.LastPromptTokens} / {ctx.ContextWindow} tokens ({ctx.UsagePercent}%)")
                .ConfigureAwait(false);
        }
    }

    private async Task HandleCompactCommandAsync(CancellationToken ct)
    {
        var ctx = _agent.Context;
        if (ctx is null)
        {
            await _output.WriteLineAsync(
                "/compact requires a configured context window. Set litellm.contextWindows.<tier> in settings.")
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var collapsed = await ctx.CompactAsync(_session, _agent.Client, ct).ConfigureAwait(false);
            await _output.WriteLineAsync(collapsed == 0
                ? "Nothing to compact — fewer than 5 user turns in history."
                : $"Compacted {collapsed} message(s) into a summary; the last 4 user turns are preserved.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync($"zdt: /compact failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task InitMemoryFileAsync(CancellationToken ct)
    {
        var path = Path.Combine(_cwd, "ZDTLLM.md");
        if (File.Exists(path))
        {
            await _output.WriteLineAsync($"ZDTLLM.md already exists at {path} — leaving it alone.")
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
        await _output.WriteLineAsync($"Created {path}").ConfigureAwait(false);
    }

    private async Task HandleModelCommandAsync(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _output.WriteLineAsync($"Current model: {_session.Model}").ConfigureAwait(false);
            await _output.WriteLineAsync("Usage: /model <name>").ConfigureAwait(false);
            return;
        }

        _session.SetModel(args.Trim());
        await _output.WriteLineAsync($"Model set to {_session.Model} (takes effect on next turn).")
            .ConfigureAwait(false);
    }

    private async Task PrintPermissionsAsync()
    {
        var rs = _agent.Permissions;
        var counts = rs.RuleCounts;
        await _output.WriteLineAsync(
                $"  rules: deny={counts.deny} ask={counts.ask} allow={counts.allow}")
            .ConfigureAwait(false);
        await _output.WriteLineAsync(
                "  Defaults: tools requiring permission (Bash, Edit, Write, WebFetch, WebSearch, Skill) Ask without an explicit allow.")
            .ConfigureAwait(false);
    }

    private async Task HandleContextCommandAsync()
    {
        var ctx = _agent.Context;
        if (ctx is null)
        {
            await _output.WriteLineAsync(
                "/context requires a configured context window. Either expose model_info on " +
                "your LiteLLM proxy (max_input_tokens) or set litellm.contextWindows.<tier> " +
                "in settings.json.")
                .ConfigureAwait(false);
            return;
        }

        await _output.WriteLineAsync("Context usage:").ConfigureAwait(false);
        await _output.WriteLineAsync().ConfigureAwait(false);

        var bar = RenderBar(ctx.LastPromptTokens, ctx.ContextWindow, width: 30);
        var totalLine = ctx.LastPromptTokens == 0
            ? $"  {bar}  0%   (no turn has run yet — usage populates after the first response)"
            : $"  {bar}  {ctx.UsagePercent}%   {ctx.LastPromptTokens:N0} / {ctx.ContextWindow:N0} tokens";
        await _output.WriteLineAsync(totalLine).ConfigureAwait(false);

        var byRole = ContextManager.EstimateTokensByRole(_session);
        if (byRole.Count > 0)
        {
            await _output.WriteLineAsync().ConfigureAwait(false);
            await _output.WriteLineAsync("  by role (estimated, 4 chars/token):").ConfigureAwait(false);

            foreach (var role in new[] { "system", "user", "assistant", "tool" })
            {
                if (!byRole.TryGetValue(role, out var roleTokens) || roleTokens == 0) continue;
                var roleBar = RenderBar(roleTokens, ctx.ContextWindow, width: 20);
                var pct = (double)roleTokens / ctx.ContextWindow * 100;
                await _output.WriteLineAsync(
                        $"    {role,-10} {roleBar}  {FormatTokens(roleTokens),8}  ({pct,5:F1}%)")
                    .ConfigureAwait(false);
            }
        }

        await _output.WriteLineAsync().ConfigureAwait(false);
        var soft = (int)(ctx.SoftThreshold * ctx.ContextWindow);
        var hard = (int)(ctx.HardThreshold * ctx.ContextWindow);
        await _output.WriteLineAsync($"  model: {_session.Model}").ConfigureAwait(false);
        await _output.WriteLineAsync(
                $"  /compact recommended at {ctx.SoftThreshold * 100:F0}% ({soft:N0} tokens)")
            .ConfigureAwait(false);
        await _output.WriteLineAsync(
                $"  auto-compact at {ctx.HardThreshold * 100:F0}% ({hard:N0} tokens)")
            .ConfigureAwait(false);
    }

    private static string RenderBar(int filled, int total, int width)
    {
        if (total <= 0 || width <= 0) return string.Empty;
        var ratio = Math.Clamp((double)filled / total, 0, 1);
        var on = (int)Math.Round(ratio * width);
        return new string('▰', on) + new string('▱', width - on);
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
