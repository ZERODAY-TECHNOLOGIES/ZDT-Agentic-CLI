using System.Net;
using System.Text;
using Zdtllm.Core;
using Zdtllm.Core.Sessions;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core.Repl;

/// <summary>
/// The REPL half of interactive queueing: after a turn finishes, any messages the user queued
/// while it ran are drained and run as follow-up turns. Uses a pre-seeded queue (no real console)
/// to keep the test deterministic — the console capture path is exercised only in real usage.
/// </summary>
public sealed class ReplQueueTests : IDisposable
{
    private readonly string _tempDir;

    public ReplQueueTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zdt-repl-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static string FinalText(string text) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n" +
        "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Queued_message_runs_as_a_followup_turn_after_the_prompt()
    {
        // Two model calls: the initial prompt, then the queued follow-up.
        var handler = new StubHandler(Sse(FinalText("first-answer")), Sse(FinalText("second-answer")));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        var queue = new UserInputQueue();
        queue.Enqueue("the queued follow-up question");

        var session = Session.NewEphemeral("test-model");
        var output = new StringWriter();
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, new StringReader("/exit\n"), output, new StringWriter(), _tempDir,
            inputQueue: queue);

        await repl.RunAsync(initialPrompt: "the initial prompt");

        handler.Requests.Should().HaveCount(2);
        handler.RequestBodies[0].Should().Contain("the initial prompt");
        handler.RequestBodies[1].Should().Contain("the queued follow-up question");
        output.ToString().Should().Contain("running queued message");
        queue.HasPending.Should().BeFalse();
    }

    [Fact]
    public async Task Persistent_session_farewell_shows_resume_command_on_exit()
    {
        var http = new HttpClient(new StubHandler());
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        using var store = SessionStore.Create(_tempDir);
        var session = Session.NewPersistent(store, "test-model");
        var output = new StringWriter();
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, new StringReader("/exit\n"), output, new StringWriter(), _tempDir);

        await repl.RunAsync();

        var text = output.ToString();
        text.Should().Contain("closed");
        text.Should().Contain($"zdt -r {session.Id}");
    }

    [Fact]
    public async Task Ephemeral_session_farewell_says_nothing_saved()
    {
        var http = new HttpClient(new StubHandler());
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        var session = Session.NewEphemeral("test-model");
        var output = new StringWriter();
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, new StringReader("/exit\n"), output, new StringWriter(), _tempDir);

        await repl.RunAsync();

        output.ToString().Should().Contain("ephemeral");
    }

    [Fact]
    public async Task Slash_plan_toggles_plan_mode_on_and_off()
    {
        var http = new HttpClient(new StubHandler());
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        var plan = new Zdtllm.Tools.PlanModeState(active: false);
        var session = Session.NewEphemeral("test-model");
        var output = new StringWriter();
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, new StringReader("/plan\n/plan\n/exit\n"),
            output, new StringWriter(), _tempDir, planMode: plan);

        await repl.RunAsync();

        // Toggled on then off → ends off. Output shows both transitions.
        plan.InPlanMode.Should().BeFalse();
        output.ToString().Should().Contain("Plan mode ON");
        output.ToString().Should().Contain("Plan mode OFF");
    }

    [Fact]
    public async Task No_queue_configured_behaves_exactly_like_before()
    {
        var handler = new StubHandler(Sse(FinalText("only-answer")));
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0, InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        var agent = new AgentLoop(client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });

        var session = Session.NewEphemeral("test-model");
        var repl = new Zdtllm.Core.Repl.Repl(
            session, agent, new StringReader("/exit\n"), new StringWriter(), new StringWriter(), _tempDir);

        await repl.RunAsync(initialPrompt: "hello");

        handler.Requests.Should().HaveCount(1);
    }
}
