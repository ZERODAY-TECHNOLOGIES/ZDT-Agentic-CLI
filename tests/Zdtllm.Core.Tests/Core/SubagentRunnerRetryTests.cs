using System.Net;
using System.Text;
using System.Text.Json;
using Zdtllm.Core;
using Zdtllm.Core.Tests.LiteLLM;
using Zdtllm.LiteLLM;
using Zdtllm.Permissions;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Core;

/// <summary>
/// Three-attempt budget per subagent dispatch:
///   1. requested type
///   2. requested type (retry, in case of transient HTTP/timeout)
///   3. general-purpose fallback (only if requested type wasn't already general-purpose)
/// Cancellation short-circuits the loop. These tests use a counted-failure HTTP handler
/// to drive each branch deterministically.
/// </summary>
public sealed class SubagentRunnerRetryTests
{
    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static HttpResponseMessage SimpleResponse(string text)
    {
        var contentJson = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text } } },
        });
        var stopJson = JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop" } },
        });
        return Sse($"data: {contentJson}\n\ndata: {stopJson}\n\ndata: [DONE]\n\n");
    }

    private static AgentLoop BuildParent(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var client = new LiteLLMClient(http, new LiteLLMClientOptions
        {
            BaseUrl = "http://stub", ApiKey = "k", MaxRetries = 0,
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });
        return new AgentLoop(
            client, new ToolRegistry(), PermissionRuleSet.Empty,
            new AgentLoopOptions { Model = "test-model" });
    }

    [Fact]
    public async Task Succeeds_on_first_attempt_without_marking_as_fallback()
    {
        var handler = new StubHandler(SimpleResponse("first try"));
        var runner = new SubagentRunner(BuildParent(handler));

        var result = await runner.RunAsync(
            new SubagentRequest("d", "do it", "code-reviewer"),
            CancellationToken.None);

        result.FinalText.Should().Be("first try");
        result.FinalText.Should().NotContain("fallback");
        // Only one round-trip — no retry needed.
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Retries_once_on_first_failure_and_succeeds_on_second_attempt()
    {
        // First request errors, second one succeeds.
        var handler = new FlakyHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") },
            SimpleResponse("recovered"));
        var runner = new SubagentRunner(BuildParent(handler));

        var result = await runner.RunAsync(
            new SubagentRequest("d", "do it", "code-reviewer"),
            CancellationToken.None);

        result.FinalText.Should().Be("recovered");
        result.FinalText.Should().NotContain("fallback");
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Falls_back_to_general_purpose_when_requested_type_fails_twice()
    {
        // Two failures of the requested type, then a success — which the runner attempts as
        // general-purpose. The result text should announce the fallback.
        var handler = new FlakyHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom1") },
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom2") },
            SimpleResponse("general-purpose to the rescue"));
        var runner = new SubagentRunner(BuildParent(handler));

        var result = await runner.RunAsync(
            new SubagentRequest("d", "do it", "code-reviewer"),
            CancellationToken.None);

        result.FinalText.Should().Contain("fallback to general-purpose");
        result.FinalText.Should().Contain("general-purpose to the rescue");
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Throws_subagent_execution_exception_when_all_attempts_fail()
    {
        var handler = new FlakyHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("e1") },
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("e2") },
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("e3") });
        var runner = new SubagentRunner(BuildParent(handler));

        var act = async () => await runner.RunAsync(
            new SubagentRequest("d", "do it", "code-reviewer"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SubagentExecutionException>();
        ex.Which.Message.Should().Contain("3 attempt");
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task General_purpose_request_only_gets_two_attempts_no_fallback()
    {
        // When the request is already general-purpose there is no fallback type to escalate
        // to — we cap at two attempts (initial + retry).
        var handler = new FlakyHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("e1") },
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("e2") });
        var runner = new SubagentRunner(BuildParent(handler));

        var act = async () => await runner.RunAsync(
            new SubagentRequest("d", "do it", "general-purpose"),
            CancellationToken.None);

        await act.Should().ThrowAsync<SubagentExecutionException>();
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Cancellation_short_circuits_retry_loop()
    {
        // Cancel before any call lands. The runner should not call the LLM at all.
        var handler = new FlakyHandler(SimpleResponse("never"));
        var runner = new SubagentRunner(BuildParent(handler));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await runner.RunAsync(
            new SubagentRequest("d", "do it", "code-reviewer"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(0);
    }

    /// <summary>
    /// Returns a queued sequence of HTTP responses; throws when exhausted. Counts every
    /// SendAsync invocation. Non-2xx responses cause LiteLLMClient.StreamChatAsync to throw
    /// HttpRequestException, which the retry loop treats as a transient failure.
    /// </summary>
    private sealed class FlakyHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        private int _callCount;

        public FlakyHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (_responses.Count == 0)
                throw new InvalidOperationException("FlakyHandler ran out of queued responses.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
