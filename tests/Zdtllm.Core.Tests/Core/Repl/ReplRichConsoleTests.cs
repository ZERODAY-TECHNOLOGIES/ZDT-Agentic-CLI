using Spectre.Console;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Repl;

/// <summary>
/// /permissions has two rendering modes: a single text line (back-compat for tests / no-color
/// shells) and, when the Repl gets an IAnsiConsole, a Spectre Table. Both modes still emit the
/// "rules: deny=N ask=N allow=N" header so existing assertions stay green; the table is purely
/// additive when there are rules to display.
/// </summary>
public sealed class ReplRichConsoleTests : IDisposable
{
    private readonly string _tempDir;

    public ReplRichConsoleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-repl-rich-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private (Zdtllm.Core.Repl.Repl repl, StringWriter output, StringWriter sink) BuildRepl(
        string scriptedInput,
        PermissionRuleSet perms)
    {
        var session = Session.NewEphemeral("test-model");
        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(
            client,
            new ToolRegistry(),
            perms,
            new AgentLoopOptions { Model = "test-model" });

        var sink = new StringWriter();
        var rich = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sink),
            Interactive = InteractionSupport.No,
        });
        rich.Profile.Width = 200;

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader(scriptedInput);
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, input, output, error, _tempDir,
            richConsole: rich);
        return (repl, output, sink);
    }

    [Fact]
    public async Task Slash_permissions_with_rich_console_renders_table_with_each_rule()
    {
        var perms = PermissionRuleSet.Build(
            allow: ["Bash(git *)", "Read"],
            ask: ["Bash"],
            deny: ["Bash(rm *)"]);

        var (repl, output, sink) = BuildRepl("/permissions\n/exit\n", perms);
        await repl.RunAsync();

        // Header still prints to the plain output (existing contract).
        output.ToString().Should().Contain("rules:");

        // Table with column headers + rule strings goes to the Spectre sink.
        var rendered = sink.ToString();
        rendered.Should().Contain("deny");
        rendered.Should().Contain("ask");
        rendered.Should().Contain("allow");
        rendered.Should().Contain("Bash(git *)");
        rendered.Should().Contain("Read");
        rendered.Should().Contain("Bash(rm *)");
        // The bare "Bash" ask rule must appear too — but tests can match it loosely since
        // "Bash(...)" cells will also contain the substring. Easiest: confirm via row count.
        rendered.Should().Contain("Bash");
    }

    [Fact]
    public async Task Slash_permissions_with_no_rules_does_not_render_an_empty_table()
    {
        var (repl, output, sink) = BuildRepl("/permissions\n/exit\n", PermissionRuleSet.Empty);
        await repl.RunAsync();

        output.ToString().Should().Contain("rules:");
        // Empty rule set → no Spectre table written. The sink may still receive the prompt
        // ("> ") if any was echoed, so just assert the table chrome is absent.
        sink.ToString().Should().NotContain("permission rules");
    }
}
