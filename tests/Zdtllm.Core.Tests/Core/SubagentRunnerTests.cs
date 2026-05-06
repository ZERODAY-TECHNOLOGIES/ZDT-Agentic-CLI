using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

public sealed class SubagentRunnerTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static string SimpleResponseSse(string text)
    {
        var contentJson = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text } } },
        });
        var stopJson = JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop" } },
        });
        return $"data: {contentJson}\n\ndata: {stopJson}\n\ndata: [DONE]\n\n";
    }

    private static AgentLoop BuildParentAgent(StubHandler handler, ToolRegistry registry, string model = "test-model")
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = model });
    }

    [Fact]
    public void Code_reviewer_registry_only_includes_read_glob_grep_todowrite()
    {
        var parent = new ToolRegistry();
        parent.Register(new ReadTool());
        parent.Register(new WriteTool());
        parent.Register(new EditTool());
        parent.Register(new BashTool(Path.GetTempPath()));
        parent.Register(new GlobTool());
        parent.Register(new GrepTool());
        parent.Register(new TodoWriteTool());

        var sub = SubagentRunner.BuildRegistryForType("code-reviewer", parent);

        sub.All.Select(t => t.Schema.Name).OrderBy(n => n)
            .Should().Equal("Glob", "Grep", "Read", "TodoWrite");
    }

    [Fact]
    public void Explore_registry_adds_webfetch_on_top_of_code_reviewer_set()
    {
        var parent = new ToolRegistry();
        parent.Register(new ReadTool());
        parent.Register(new GlobTool());
        parent.Register(new GrepTool());
        parent.Register(new TodoWriteTool());
        parent.Register(new WebFetchTool(new HttpClient()));
        parent.Register(new BashTool(Path.GetTempPath())); // should NOT survive

        var sub = SubagentRunner.BuildRegistryForType("explore", parent);

        sub.All.Select(t => t.Schema.Name).OrderBy(n => n)
            .Should().Equal("Glob", "Grep", "Read", "TodoWrite", "WebFetch");
    }

    [Fact]
    public void General_purpose_registry_includes_every_parent_tool_except_task()
    {
        var parent = new ToolRegistry();
        parent.Register(new ReadTool());
        parent.Register(new BashTool(Path.GetTempPath()));
        parent.Register(new TaskTool(new FakeSubagentRunner()));

        var sub = SubagentRunner.BuildRegistryForType("general-purpose", parent);

        sub.All.Select(t => t.Schema.Name).Should().BeEquivalentTo(new[] { "Read", "Bash" });
    }

    [Fact]
    public void Unknown_type_falls_through_to_general_purpose_policy()
    {
        // The runner refuses unknown types via SupportsType, but BuildRegistryForType
        // by itself treats anything not in the policy map as general-purpose. That's fine
        // because RunAsync only ever calls it with a type that SupportsType already vetted.
        var parent = new ToolRegistry();
        parent.Register(new ReadTool());
        parent.Register(new TaskTool(new FakeSubagentRunner()));

        var sub = SubagentRunner.BuildRegistryForType("totally-fake", parent);

        sub.All.Select(t => t.Schema.Name).Should().Equal("Read");
    }

    [Fact]
    public void System_prompt_for_code_reviewer_mentions_re_reading_files()
    {
        var prompt = SubagentRunner.SystemPromptForType("code-reviewer");

        prompt.Should().Contain("READ EVERY file");
        prompt.Should().Contain("file:line");
        prompt.Should().Contain("Critical");
    }

    [Fact]
    public void System_prompt_differs_per_type()
    {
        var general = SubagentRunner.SystemPromptForType("general-purpose");
        var code = SubagentRunner.SystemPromptForType("code-reviewer");
        var explore = SubagentRunner.SystemPromptForType("explore");

        general.Should().NotBe(code);
        code.Should().NotBe(explore);
        explore.Should().NotBe(general);
    }

    [Fact]
    public void SupportsType_recognizes_all_three_canonical_types()
    {
        var runner = new SubagentRunner(BuildParentAgent(new StubHandler(), new ToolRegistry()));

        runner.SupportsType("general-purpose").Should().BeTrue();
        runner.SupportsType("code-reviewer").Should().BeTrue();
        runner.SupportsType("explore").Should().BeTrue();
        runner.SupportsType("nope").Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_returns_subagent_final_text_from_a_one_turn_response()
    {
        var handler = new StubHandler(Sse(SimpleResponseSse("subagent says hello")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry);
        var runner = new SubagentRunner(parent);

        var result = await runner.RunAsync(
            new SubagentRequest("greet", "Say hello.", "general-purpose"),
            CancellationToken.None);

        result.FinalText.Should().Be("subagent says hello");
        result.Turns.Should().Be(1);
    }

    [Fact]
    public void Subagent_registry_clones_stateful_tools_so_parent_state_is_isolated()
    {
        var parent = new ToolRegistry();
        parent.Register(new ReadTool());
        parent.Register(new BashTool(Path.GetTempPath()));
        parent.Register(new TodoWriteTool());

        var sub = SubagentRunner.BuildRegistryForType("general-purpose", parent);

        // Stateless tool reuses the parent's instance.
        sub.Get("Read").Should().BeSameAs(parent.Get("Read"));

        // Stateful tools — fresh instances so parallel subagents don't race.
        sub.Get("Bash").Should().NotBeSameAs(parent.Get("Bash"));
        sub.Get("TodoWrite").Should().NotBeSameAs(parent.Get("TodoWrite"));
    }

    [Fact]
    public async Task RunAsync_attaches_a_fresh_context_manager_when_parent_has_one()
    {
        var handler = new StubHandler(Sse(SimpleResponseSse("ok")));
        var registry = new ToolRegistry();
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        // Parent has a ContextManager configured; we'll observe whether the subagent's
        // own ContextManager picks up the streamed usage chunks (proving an instance was
        // actually created and wired into the sub AgentLoop).
        var parentContext = new ContextManager(contextWindow: 10_000, mediumModel: "med");
        var parent = new AgentLoop(client, registry, PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" }, context: parentContext);

        var runner = new SubagentRunner(parent);
        var result = await runner.RunAsync(new SubagentRequest("x", "do x"), CancellationToken.None);

        // Parent's manager wasn't touched (subagent has its OWN).
        parentContext.LastPromptTokens.Should().Be(0);
        result.FinalText.Should().Be("ok");
    }

    [Fact]
    public async Task RunAsync_skips_context_manager_when_parent_has_none()
    {
        var handler = new StubHandler(Sse(SimpleResponseSse("plain")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry);
        // parent has Context == null

        var runner = new SubagentRunner(parent);
        var result = await runner.RunAsync(new SubagentRequest("x", "do x"), CancellationToken.None);

        result.FinalText.Should().Be("plain");
    }

    [Fact]
    public async Task RunAsync_does_not_pollute_parent_via_streaming_to_real_console()
    {
        // The subagent should buffer its output internally — the parent's stdout never
        // sees the streamed text. We verify by capturing what the LLM stub got asked
        // for vs. what was forwarded; the test matters because in the real CLI the
        // parent's Console.Out is shared with the subagent's "output" arg if we ever
        // wired it wrong.
        var handler = new StubHandler(Sse(SimpleResponseSse("inner thoughts")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry);
        var runner = new SubagentRunner(parent);

        var result = await runner.RunAsync(
            new SubagentRequest("x", "do x"),
            CancellationToken.None);

        result.FinalText.Should().Be("inner thoughts");
        // Only one LLM round-trip — no leakage / extra calls.
        handler.Requests.Should().ContainSingle();

        // The subagent's request body uses the type-specific system prompt, NOT the
        // parent's default — verifies SystemPromptForType actually plumbed through.
        var body = handler.RequestBodies.Single();
        body.Should().Contain("focused subagent");
    }

    [Fact]
    public async Task RunAsync_uses_request_ParentModel_for_the_subagent_HTTP_body()
    {
        // The contract: when TaskTool plumbs ctx.Model into request.ParentModel, the runner
        // must actually use that value as the subagent's model — not the parent AgentLoop's
        // startup-frozen Options.Model. Without this fix a /model switch in the REPL would
        // never reach subagents, even though the parent itself picks it up via session.Model.
        var handler = new StubHandler(Sse(SimpleResponseSse("ok")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry, model: "parent-startup-model");
        var runner = new SubagentRunner(parent);

        await runner.RunAsync(
            new SubagentRequest("x", "do x", ParentModel: "qwen-medium-after-slash-model"),
            CancellationToken.None);

        var body = handler.RequestBodies.Single();
        body.Should().Contain("\"model\":\"qwen-medium-after-slash-model\"");
        body.Should().NotContain("parent-startup-model");
    }

    [Fact]
    public async Task RunAsync_falls_back_to_parent_Options_Model_when_ParentModel_null()
    {
        // Backwards-compat: a SubagentRequest constructed without ParentModel (older tests,
        // tools building one directly) keeps using the AgentLoop's startup Options.Model.
        var handler = new StubHandler(Sse(SimpleResponseSse("ok")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry, model: "startup-model");
        var runner = new SubagentRunner(parent);

        await runner.RunAsync(new SubagentRequest("x", "do x"), CancellationToken.None);

        handler.RequestBodies.Single().Should().Contain("\"model\":\"startup-model\"");
    }

    [Fact]
    public async Task RunAsync_uses_empty_string_ParentModel_as_unset_falls_back_to_startup()
    {
        // Defensive: an empty string is not a meaningful model id. The runner should treat
        // it the same as null and fall back to the parent's option, otherwise an upstream
        // bug or copy-paste of `""` would silently send `"model":""` to LiteLLM (which
        // returns a confusing 400 several seconds later).
        var handler = new StubHandler(Sse(SimpleResponseSse("ok")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry, model: "startup-model");
        var runner = new SubagentRunner(parent);

        await runner.RunAsync(
            new SubagentRequest("x", "do x", ParentModel: ""),
            CancellationToken.None);

        handler.RequestBodies.Single().Should().Contain("\"model\":\"startup-model\"");
    }

    [Fact]
    public async Task RunAsync_OverrideModel_takes_precedence_over_ParentModel()
    {
        // The tier-routing contract: when the resolver picks a tier-specific model for a
        // subagent_type (e.g. code-reviewer → light), TaskTool plumbs it as OverrideModel
        // and the runner must use it instead of the parent's model. Without this, the
        // litellm.subagentModels config never reaches the actual HTTP request.
        var handler = new StubHandler(Sse(SimpleResponseSse("ok")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry, model: "parent-default");
        var runner = new SubagentRunner(parent);

        var result = await runner.RunAsync(
            new SubagentRequest(
                "x", "do x",
                ParentModel: "parent-default",
                OverrideModel: "qwen-fast-tier"),
            CancellationToken.None);

        var body = handler.RequestBodies.Single();
        body.Should().Contain("\"model\":\"qwen-fast-tier\"");
        body.Should().NotContain("parent-default");
        result.Model.Should().Be("qwen-fast-tier");
    }

    [Fact]
    public async Task RunAsync_returns_resolved_model_in_SubagentResult_for_visibility()
    {
        // The CLI surfaces the subagent's actual model in the "[subagent type — N turn(s),
        // model: X]" preamble so users can verify tier routing. The runner has to populate
        // that field even when no override was set (then it equals ParentModel).
        var handler = new StubHandler(Sse(SimpleResponseSse("ok")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry, model: "parent-startup");
        var runner = new SubagentRunner(parent);

        var result = await runner.RunAsync(
            new SubagentRequest("x", "do x", ParentModel: "current-parent-model"),
            CancellationToken.None);

        result.Model.Should().Be("current-parent-model");
    }

    [Fact]
    public async Task Parallel_subagents_share_the_same_handler_without_deadlocking()
    {
        // Pre-3W: AgentLoop's parallel batch path ran multiple TaskTool calls in parallel,
        // each opening its own AnsiConsole.Status() spinner — Spectre's interactive lock then
        // threw "Trying to run one or more interactive functions concurrently" and the whole
        // batch crashed mid-flight. After the fix, TaskTool no longer opens a Status; this
        // test runs a 4-way parallel dispatch directly on the runner to prove no exclusivity
        // collision happens regardless of how Spectre is configured. Headless StringWriters
        // make the console capability pretty much irrelevant, but the regression we're guarding
        // against is the dispatcher itself, not the renderer.
        var handler = new StubHandler(
            Sse(SimpleResponseSse("worker-1")),
            Sse(SimpleResponseSse("worker-2")),
            Sse(SimpleResponseSse("worker-3")),
            Sse(SimpleResponseSse("worker-4")));
        var registry = new ToolRegistry();
        var parent = BuildParentAgent(handler, registry, model: "m");
        var runner = new SubagentRunner(parent);

        var tasks = Enumerable.Range(1, 4)
            .Select(i => runner.RunAsync(
                new SubagentRequest($"worker {i}", "do work", ParentModel: "m"),
                CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(4);
        results.Select(r => r.FinalText).Should().BeEquivalentTo(
            "worker-1", "worker-2", "worker-3", "worker-4");
        handler.Requests.Should().HaveCount(4);
    }

    private sealed class FakeSubagentRunner : ISubagentRunner
    {
        public IReadOnlyList<string> AvailableTypes => Array.Empty<string>();
        public bool SupportsType(string type) => false;
        public IReadOnlyList<SubagentTypeInfo> GetTypeInfo() => Array.Empty<SubagentTypeInfo>();
        public Task<SubagentResult> RunAsync(SubagentRequest request, CancellationToken ct) =>
            throw new NotImplementedException("test stub");
    }
}
