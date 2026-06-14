namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;
using Moongazing.OrionRelay.Signing;

using Xunit;

public sealed class WebhookDispatcherTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static WebhookMessage Message() => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{\"event\":\"ping\"}"),
        EventId = "evt_1",
        EventType = "ping",
    };

    private static (WebhookDispatcher Dispatcher, WebhookDiagnostics Diagnostics) Build(
        StubHttpMessageHandler handler,
        WebhookDeliveryOptions? options = null,
        IWebhookSigner? signer = null,
        IWebhookDeliveryObserver? observer = null)
    {
        var diagnostics = new WebhookDiagnostics();
        var dispatcher = new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? new WebhookDeliveryOptions(),
            diagnostics,
            signer,
            observer,
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.0,
            now: () => FixedNow);
        return (dispatcher, diagnostics);
    }

    [Fact]
    public async Task Succeeds_on_first_2xx()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(200, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retries_a_503_then_succeeds()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var (dispatcher, diagnostics) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(204, result.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_a_400()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.BadRequest));
        var (dispatcher, diagnostics) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(400, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Exhausts_the_attempt_budget_on_persistent_500()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var options = new WebhookDeliveryOptions { MaxAttempts = 3 };
        var observer = new RecordingObserver();
        var (dispatcher, diagnostics) = Build(handler, options, observer: observer);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, observer.Attempts);
        Assert.Equal(1, observer.Exhausted);
    }

    [Fact]
    public async Task Retries_a_transport_fault()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Throw(new HttpRequestException("connection reset")),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task Signs_the_request_when_a_signer_is_supplied()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler, signer: new WebhookSigner("secret"));
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.TryGetValues("Orion-Signature", out var values));
        Assert.StartsWith("t=1700000000,v1=", values!.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_throws_rather_than_returning_a_failure()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler);
        using var _ = diagnostics;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(Message(), cts.Token));
    }

    [Fact]
    public async Task A_faulting_observer_does_not_break_delivery()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics) = Build(handler, observer: new ThrowingObserver());
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
    }

    private sealed class RecordingObserver : IWebhookDeliveryObserver
    {
        public int Attempts { get; private set; }
        public int Exhausted { get; private set; }

        public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception) => Attempts++;
        public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result) => Exhausted++;
    }

    private sealed class ThrowingObserver : IWebhookDeliveryObserver
    {
        public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception) =>
            throw new InvalidOperationException("observer boom");

        public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result) =>
            throw new InvalidOperationException("observer boom");
    }
}
