using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Zdtllm.LiteLLM;

namespace Zdtllm.Core.Tests.LiteLLM;

/// <summary>
/// Verifies LiteLLMClient surfaces HTTP 429 as a structured RateLimitException with the
/// correct resetsAtUnix hint pulled from Retry-After / x-ratelimit-reset headers. The
/// AppSec-Automator rate-limit recovery flow depends on these timestamps to schedule
/// the retry — a wrong value here would either retry too soon (still rate-limited) or
/// too late (blocking the operator unnecessarily).
/// </summary>
public sealed class RateLimitTests
{
    private static HttpResponseMessage TooManyWithRetryAfterDelta(int seconds)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limit hit", Encoding.UTF8, "text/plain"),
        };
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return resp;
    }

    private static HttpResponseMessage TooManyWithXRatelimitReset(long unixSeconds)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limit hit", Encoding.UTF8, "text/plain"),
        };
        resp.Headers.TryAddWithoutValidation("x-ratelimit-reset", unixSeconds.ToString());
        return resp;
    }

    private static HttpResponseMessage TooManyBare() =>
        new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limit hit", Encoding.UTF8, "text/plain"),
        };

    private static LiteLLMClient BuildClient(StubHandler handler, int maxRetries) =>
        new(
            new HttpClient(handler),
            new LiteLLMClientOptions
            {
                BaseUrl = "http://stub",
                ApiKey = "k",
                MaxRetries = maxRetries,
                InitialBackoff = TimeSpan.FromMilliseconds(1),
            });

    [Fact]
    public async Task Persistent_429_with_Retry_After_throws_RateLimitException_with_resetsAtUnix()
    {
        // 0 retries — first 429 = final 429. Retry-After: 60s.
        var handler = new StubHandler(TooManyWithRetryAfterDelta(60));
        var client = BuildClient(handler, maxRetries: 0);

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Func<Task> act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync(
                new[] { ChatMessage.User("hi") }, tools: null, "m")) { /* drain */ }
        };
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5;

        var ex = await act.Should().ThrowAsync<RateLimitException>();
        var reset = ex.Which.ResetsAtUnix;

        // resetsAt should land in [now+55, now+65] — Retry-After delta of 60s, with a
        // bit of slack for clock granularity / test scheduling.
        reset.Should().NotBeNull();
        reset!.Value.Should().BeInRange(before + 55, after + 65);
    }

    [Fact]
    public async Task Persistent_429_with_x_ratelimit_reset_uses_that_unix_timestamp()
    {
        var fixedReset = DateTimeOffset.UtcNow.AddSeconds(120).ToUnixTimeSeconds();
        var handler = new StubHandler(TooManyWithXRatelimitReset(fixedReset));
        var client = BuildClient(handler, maxRetries: 0);

        Func<Task> act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync(
                new[] { ChatMessage.User("hi") }, tools: null, "m")) { /* drain */ }
        };
        var ex = await act.Should().ThrowAsync<RateLimitException>();
        ex.Which.ResetsAtUnix.Should().Be(fixedReset);
    }

    [Fact]
    public async Task Persistent_429_without_any_reset_hint_throws_with_null_resetsAt()
    {
        var handler = new StubHandler(TooManyBare());
        var client = BuildClient(handler, maxRetries: 0);

        Func<Task> act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync(
                new[] { ChatMessage.User("hi") }, tools: null, "m")) { /* drain */ }
        };
        var ex = await act.Should().ThrowAsync<RateLimitException>();
        ex.Which.ResetsAtUnix.Should().BeNull();
    }

    [Fact]
    public async Task Mixed_failure_then_429_still_throws_RateLimitException_for_the_terminal_429()
    {
        // First a transient 503, then a persistent 429 on the retries. Final attempt was
        // the 429 → we want the structured RateLimitException, not the generic wrap.
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("oops") },
            TooManyWithRetryAfterDelta(30),
            TooManyWithRetryAfterDelta(45)); // last attempt → 429
        var client = BuildClient(handler, maxRetries: 2);

        Func<Task> act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync(
                new[] { ChatMessage.User("hi") }, tools: null, "m")) { /* drain */ }
        };
        var ex = await act.Should().ThrowAsync<RateLimitException>();
        // Should reflect the LAST 429's hint (Retry-After: 45s), not the 30s from the middle attempt.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ex.Which.ResetsAtUnix.Should().BeInRange(now + 40, now + 50);
    }

    [Fact]
    public async Task Final_attempt_is_a_5xx_not_a_429_throws_generic_LiteLLMException()
    {
        // 429 first, 503 last → terminal failure was a 5xx, not a 429. We should NOT
        // surface RateLimitException because the bucket may be fine — the proxy itself
        // is broken. AppSec-Automator's DetectsRateLimit relies on the explicit 429
        // signal, so misclassifying a 5xx as a rate-limit would mislead it.
        var handler = new StubHandler(
            TooManyWithRetryAfterDelta(60),
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("upstream borked") });
        var client = BuildClient(handler, maxRetries: 1);

        Func<Task> act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync(
                new[] { ChatMessage.User("hi") }, tools: null, "m")) { /* drain */ }
        };
        var ex = await act.Should().ThrowAsync<LiteLLMException>();
        ex.Which.Should().NotBeOfType<RateLimitException>();
    }
}
