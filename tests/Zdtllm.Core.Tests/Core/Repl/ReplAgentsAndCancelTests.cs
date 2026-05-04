using System.Net;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Repl;

/// <summary>
/// /agents lists the runner's profiles, and Repl.CancelCurrentTurn aborts whatever
/// turn is currently in flight (used by the Cli's Ctrl+C handler).
/// </summary>
public sealed class ReplAgentsAndCancelTests : IDisposable
{
    private readonly string _tempDir;

    public ReplAgentsAndCancelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-repl-3j-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    [Fact]
    public async Task Slash_agents_lists_runner_types_in_plain_mode()
    {
        var session = Session.NewEphemeral("test-model");
        var http = new HttpClient(new StubHandler());
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });
        var runner = new FakeRunner();

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("/agents\n/exit\n");
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, input, output, error, _tempDir,
            subagentRunner: runner);

        await repl.RunAsync();

        var text = output.ToString();
        text.Should().Contain("Subagent profiles:");
        text.Should().Contain("general-purpose");
        text.Should().Contain("all (except Task)");
        text.Should().Contain("code-reviewer");
        text.Should().Contain("Read-only review");
        text.Should().Contain("Read");
        text.Should().Contain("Glob");
    }

    [Fact]
    public async Task Slash_agents_with_no_runner_explains_how_to_enable()
    {
        var session = Session.NewEphemeral("test-model");
        var http = new HttpClient(new StubHandler());
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        var output = new StringWriter();
        var input = new StringReader("/agents\n/exit\n");
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, input, output, new StringWriter(), _tempDir);

        await repl.RunAsync();

        output.ToString().Should().Contain("/agents requires the Agent tool");
    }

    [Fact]
    public async Task Slash_agents_with_rich_console_renders_a_table()
    {
        var session = Session.NewEphemeral("test-model");
        var http = new HttpClient(new StubHandler());
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });
        var runner = new FakeRunner();

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
        var input = new StringReader("/agents\n/exit\n");
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, input, output, new StringWriter(), _tempDir,
            richConsole: rich, subagentRunner: runner);

        await repl.RunAsync();

        var rendered = sink.ToString();
        rendered.Should().Contain("subagent types");
        rendered.Should().Contain("general-purpose");
        rendered.Should().Contain("code-reviewer");
        rendered.Should().Contain("type");
        rendered.Should().Contain("description");
        rendered.Should().Contain("tools");
    }

    [Fact]
    public async Task CancelCurrentTurn_halts_the_in_flight_turn_and_REPL_continues()
    {
        // The stub LLM streams forever (or at least long enough to be cancelled). We then
        // call CancelCurrentTurn from a side task and verify the REPL prints "(turn cancelled)"
        // and then exits cleanly when /exit is read.
        var session = Session.NewEphemeral("test-model");
        var slowHandler = new SlowHandler(delayMs: 5_000);
        var http = new HttpClient(slowHandler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model", MaxTurns = 1 });

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("hello\n/exit\n");
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, input, output, error, _tempDir);

        // Fire the cancel after a short delay — the turn is still in its HTTP request when
        // this trips, which mimics Ctrl+C while the model is "thinking".
        var cancelTask = Task.Run(async () =>
        {
            await Task.Delay(100);
            repl.CancelCurrentTurn();
        });

        var exit = await repl.RunAsync();

        exit.Should().Be(0);
        await cancelTask;
        error.ToString().Should().Contain("turn cancelled");
    }

    private sealed class FakeRunner : ISubagentRunner
    {
        public IReadOnlyList<string> AvailableTypes { get; } =
            new[] { "general-purpose", "code-reviewer", "explore" };
        public bool SupportsType(string type) => AvailableTypes.Contains(type, StringComparer.Ordinal);
        public IReadOnlyList<SubagentTypeInfo> GetTypeInfo() => new[]
        {
            new SubagentTypeInfo("general-purpose",
                "All tools the parent has, except Task itself (no recursive sub-spawning).",
                new[] { "*" }),
            new SubagentTypeInfo("code-reviewer",
                "Read-only review profile — analyses code without ever modifying it.",
                new[] { "Glob", "Grep", "Read", "TodoWrite" }),
            new SubagentTypeInfo("explore",
                "Read-only research profile — local FS plus web fetch for sourced answers.",
                new[] { "Glob", "Grep", "Read", "TodoWrite", "WebFetch" }),
        };
        public Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct) =>
            throw new NotSupportedException("test stub");
    }

    /// <summary>
    /// Holds the request open for delayMs unless the caller cancels — mimics a slow LLM
    /// stream so the test can race CancelCurrentTurn against the in-flight HTTP send.
    /// </summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly int _delayMs;

        public SlowHandler(int delayMs) { _delayMs = delayMs; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
