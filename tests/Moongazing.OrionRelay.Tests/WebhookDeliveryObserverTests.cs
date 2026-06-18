namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;

using Xunit;

/// <summary>
/// Coverage for the <see cref="IWebhookDeliveryObserver"/> contract: that OnAttempt fires per
/// attempt with the right status/exception, OnExhausted fires once and only on abandonment, and
/// that the no-op default and a faulting observer never disturb delivery.
/// </summary>
public sealed class WebhookDeliveryObserverTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static WebhookMessage Message() => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{}"),
        EventType = "ping",
    };

    private static (WebhookDispatcher Dispatcher, WebhookDiagnostics Diagnostics) Build(
        StubHttpMessageHandler handler, IWebhookDeliveryObserver observer, WebhookDeliveryOptions? options = null)
    {
        var diagnostics = new WebhookDiagnostics();
        var dispatcher = new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? new WebhookDeliveryOptions(),
            diagnostics,
            signer: null,
            observer: observer,
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.0,
            now: () => FixedNow);
        return (dispatcher, diagnostics);
    }

    [Fact]
    public async Task OnAttempt_fires_once_on_first_success_with_the_status_and_no_exception()
    {
        var observer = new CapturingObserver();
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler, observer);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        var attempt = Assert.Single(observer.Attempts);
        Assert.Equal(1, attempt.Attempt);
        Assert.Equal(200, attempt.StatusCode);
        Assert.Null(attempt.Exception);
        Assert.Empty(observer.Exhaustions); // success never exhausts
    }

    [Fact]
    public async Task OnAttempt_reports_the_status_for_each_retryable_then_final_attempt()
    {
        var observer = new CapturingObserver();
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Status(HttpStatusCode.TooManyRequests),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler, observer);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        Assert.Equal(3, observer.Attempts.Count);
        Assert.Equal([1, 2, 3], observer.Attempts.Select(a => a.Attempt));
        Assert.Equal([503, 429, 200], observer.Attempts.Select(a => a.StatusCode));
        Assert.All(observer.Attempts, a => Assert.Null(a.Exception));
    }

    [Fact]
    public async Task OnAttempt_carries_a_null_status_and_the_transport_exception_on_a_fault()
    {
        var boom = new HttpRequestException("reset");
        var observer = new CapturingObserver();
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Throw(boom),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler, observer);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        Assert.Equal(2, observer.Attempts.Count);
        Assert.Null(observer.Attempts[0].StatusCode);
        Assert.Same(boom, observer.Attempts[0].Exception);
        Assert.Equal(200, observer.Attempts[1].StatusCode);
    }

    [Fact]
    public async Task OnExhausted_fires_exactly_once_with_the_terminal_failure_result()
    {
        var observer = new CapturingObserver();
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var (dispatcher, diagnostics) = Build(handler, observer, new WebhookDeliveryOptions { MaxAttempts = 2 });
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        var exhausted = Assert.Single(observer.Exhaustions);
        Assert.Same(result, exhausted.Result);
        Assert.False(exhausted.Result.Succeeded);
        Assert.Equal(2, exhausted.Result.Attempts);
        Assert.Equal(500, exhausted.Result.StatusCode);
    }

    [Fact]
    public async Task OnExhausted_fires_once_even_when_a_fatal_status_ends_the_delivery_on_the_first_attempt()
    {
        var observer = new CapturingObserver();
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.BadRequest));
        var (dispatcher, diagnostics) = Build(handler, observer);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        // OnExhausted is named for budget exhaustion, but the source calls it for ANY non-success
        // terminal result, including a fatal 4xx on the very first attempt. Asserting the real
        // behaviour: a single OnExhausted fires here too. (Worth a doc clarification in src; the
        // callback name implies retries were exhausted, which is not strictly the case for a fatal.)
        var exhausted = Assert.Single(observer.Exhaustions);
        Assert.Same(result, exhausted.Result);
        Assert.Equal(1, exhausted.Result.Attempts);
        Assert.Equal(400, exhausted.Result.StatusCode);
        Assert.Single(observer.Attempts);
    }

    [Fact]
    public async Task The_same_message_instance_is_passed_to_every_callback()
    {
        var observer = new CapturingObserver();
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var (dispatcher, diagnostics) = Build(handler, observer, new WebhookDeliveryOptions { MaxAttempts = 2 });
        using var _ = diagnostics;

        var message = Message();
        await dispatcher.DispatchAsync(message);

        Assert.All(observer.Attempts, a => Assert.Same(message, a.Message));
        Assert.All(observer.Exhaustions, e => Assert.Same(message, e.Message));
    }

    [Fact]
    public async Task The_null_observer_default_is_a_no_op_and_delivery_still_succeeds()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        using var diagnostics = new WebhookDiagnostics();
        var dispatcher = new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new WebhookDeliveryOptions(),
            diagnostics,
            signer: null,
            observer: null,
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.0,
            now: () => FixedNow);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_faulting_observer_on_exhausted_does_not_mask_the_failure_result()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var (dispatcher, diagnostics) = Build(handler, new ThrowingObserver(), new WebhookDeliveryOptions { MaxAttempts = 2 });
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Attempts);
    }

    private sealed class CapturingObserver : IWebhookDeliveryObserver
    {
        public List<AttemptRecord> Attempts { get; } = [];
        public List<ExhaustionRecord> Exhaustions { get; } = [];

        public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception) =>
            Attempts.Add(new AttemptRecord(message, attempt, statusCode, exception));

        public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result) =>
            Exhaustions.Add(new ExhaustionRecord(message, result));

        public readonly record struct AttemptRecord(WebhookMessage Message, int Attempt, int? StatusCode, Exception? Exception);

        public readonly record struct ExhaustionRecord(WebhookMessage Message, WebhookDeliveryResult Result);
    }

    private sealed class ThrowingObserver : IWebhookDeliveryObserver
    {
        public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception) =>
            throw new InvalidOperationException("boom");

        public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result) =>
            throw new InvalidOperationException("boom");
    }
}
