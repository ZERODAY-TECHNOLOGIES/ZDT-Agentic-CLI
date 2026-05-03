using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Repl;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Repl;

public sealed class ReplTests : IDisposable
{
    private readonly string _tempDir;

    public ReplTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-repl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private (Zdtllm.Core.Repl.Repl repl, Session session, StringWriter output, StringWriter error)
        BuildRepl(string scriptedInput, params HttpResponseMessage[] llmResponses)
    {
        var session = Session.NewEphemeral("test-model");

        var handler = new StubHandler(llmResponses);
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(
            client,
            new ToolRegistry(),
            PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        var input = new StringReader(scriptedInput);
        var output = new StringWriter();
        var error = new StringWriter();
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, input, output, error, _tempDir);
        return (repl, session, output, error);
    }

    private static HttpResponseMessage SimpleTextResponse(string text)
    {
        var sse =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = text } } },
            }) + "\n\n" +
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop" } },
            }) + "\n\n" +
            "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
    }

    [Fact]
    public async Task Help_lists_commands_and_REPL_keeps_running()
    {
        var (repl, _, output, _) = BuildRepl("/help\n/exit\n");

        var exit = await repl.RunAsync();

        exit.Should().Be(0);
        var text = output.ToString();
        text.Should().Contain("/exit");
        text.Should().Contain("/clear");
        text.Should().Contain("/status");
        text.Should().Contain("/init");
        text.Should().Contain("/model");
        text.Should().Contain("/permissions");
    }

    [Fact]
    public async Task Slash_exit_returns_zero_immediately()
    {
        var (repl, _, _, _) = BuildRepl("/exit\n");

        var exit = await repl.RunAsync();

        exit.Should().Be(0);
    }

    [Fact]
    public async Task Slash_quit_alias_also_exits()
    {
        var (repl, _, _, _) = BuildRepl("/quit\n");

        (await repl.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Eof_exits_cleanly()
    {
        var (repl, _, _, _) = BuildRepl(""); // empty input → ReadLineAsync returns null

        (await repl.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Slash_clear_drops_user_and_assistant_messages_keeping_system()
    {
        var (repl, session, _, _) = BuildRepl("/clear\n/exit\n");
        // Pre-seed conversation history (no agent involvement)
        session.AddSystem("you are zdt");
        session.AddUser("first question");
        session.AddAssistant("first answer");

        await repl.RunAsync();

        session.Messages.Should().ContainSingle();
        session.Messages[0].Role.Should().Be("system");
    }

    [Fact]
    public async Task Slash_status_prints_session_id_and_model()
    {
        var (repl, session, output, _) = BuildRepl("/status\n/exit\n");

        await repl.RunAsync();

        var text = output.ToString();
        text.Should().Contain($"session: {session.Id}");
        text.Should().Contain("model: test-model");
        text.Should().Contain("messages: 0");
    }

    [Fact]
    public async Task Slash_init_creates_ZDTLLM_md_in_cwd()
    {
        var (repl, _, output, _) = BuildRepl("/init\n/exit\n");

        await repl.RunAsync();

        var path = Path.Combine(_tempDir, "ZDTLLM.md");
        File.Exists(path).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().Contain("# ZDTLLM.md");
        contents.Should().Contain("zer0day.ro");
        output.ToString().Should().Contain("Created");
    }

    [Fact]
    public async Task Slash_init_does_not_overwrite_existing_file()
    {
        var path = Path.Combine(_tempDir, "ZDTLLM.md");
        await File.WriteAllTextAsync(path, "EXISTING");

        var (repl, _, output, _) = BuildRepl("/init\n/exit\n");

        await repl.RunAsync();

        (await File.ReadAllTextAsync(path)).Should().Be("EXISTING");
        output.ToString().Should().Contain("already exists");
    }

    [Fact]
    public async Task Slash_model_changes_session_model_for_next_turn()
    {
        var (repl, session, output, _) = BuildRepl("/model swapped-model\n/exit\n");

        await repl.RunAsync();

        session.Model.Should().Be("swapped-model");
        output.ToString().Should().Contain("Model set to swapped-model");
    }

    [Fact]
    public async Task Slash_model_without_arg_prints_current_model()
    {
        var (repl, _, output, _) = BuildRepl("/model\n/exit\n");

        await repl.RunAsync();

        output.ToString().Should().Contain("Current model: test-model");
    }

    [Fact]
    public async Task Slash_permissions_prints_rule_counts()
    {
        var (repl, _, output, _) = BuildRepl("/permissions\n/exit\n");

        await repl.RunAsync();

        output.ToString().Should().Contain("rules:");
        output.ToString().Should().Contain("deny=0 ask=0 allow=0");
    }

    [Fact]
    public async Task Unknown_command_prints_message_and_keeps_running()
    {
        var (repl, _, output, _) = BuildRepl("/nope\n/exit\n");

        var exit = await repl.RunAsync();

        exit.Should().Be(0);
        output.ToString().Should().Contain("Unknown command");
        output.ToString().Should().Contain("/help");
    }

    [Fact]
    public async Task Empty_lines_are_skipped_without_calling_agent()
    {
        var (repl, _, _, _) = BuildRepl("\n\n   \n/exit\n");
        // No LLM responses queued — if the empty lines accidentally triggered a turn,
        // StubHandler would throw "out of responses" and the test would fail.

        (await repl.RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Initial_prompt_is_processed_before_reading_input()
    {
        var (repl, session, output, _) = BuildRepl("/exit\n", SimpleTextResponse("Hello!"));

        var exit = await repl.RunAsync(initialPrompt: "say hi");

        exit.Should().Be(0);
        // After the initial turn the session should contain: system + user("say hi") + assistant("Hello!")
        session.Messages.Select(m => m.Role).Should().Equal("system", "user", "assistant");
        session.Messages.Last(m => m.Role == "user").Content.Should().Be("say hi");
        output.ToString().Should().Contain("Hello!");
    }

    [Fact]
    public async Task Non_slash_lines_drive_agent_turns()
    {
        var (repl, session, _, _) = BuildRepl(
            "ping\n/exit\n",
            SimpleTextResponse("pong"));

        await repl.RunAsync();

        session.Messages.Where(m => m.Role == "user").Single().Content.Should().Be("ping");
        session.Messages.Last().Role.Should().Be("assistant");
        session.Messages.Last().Content.Should().Be("pong");
    }
}
