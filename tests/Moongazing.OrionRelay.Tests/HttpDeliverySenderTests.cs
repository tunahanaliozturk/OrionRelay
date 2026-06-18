namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;

using Xunit;

/// <summary>
/// Exercises the HttpClient-based delivery sender (<see cref="WebhookDispatcher"/>) against a
/// stub transport: it must retry server-error responses and surface a 2xx as success, and it must
/// fire <see cref="IWebhookDeliveryObserver.OnExhausted"/> exactly once with the final context.
/// </summary>
public sealed class HttpDeliverySenderTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static WebhookMessage Message() => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{\"event\":\"ping\"}"),
        EventType = "ping",
    };

    private static WebhookDispatcher Build(
        StubHttpMessageHandler handler,
        WebhookDiagnostics diagnostics,
        WebhookDeliveryOptions? options = null,
        IWebhookDeliveryObserver? observer = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? new WebhookDeliveryOptions(),
            diagnostics,
            signer: null,
            observer: observer,
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.0,
            now: () => FixedNow);

    [Fact]
    public async Task Posts_the_payload_to_the_endpoint()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        using var diagnostics = new WebhookDiagnostics();
        var dispatcher = Build(handler, diagnostics);

        await dispatcher.DispatchAsync(Message());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://example.test/hook"), request.RequestUri);
    }

    [Fact]
    public async Task Retries_on_5xx_then_surfaces_success_on_2xx()
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(HttpStatusCode.BadGateway),
            StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));
        using var diagnostics = new WebhookDiagnostics();
        var dispatcher = Build(handler, diagnostics);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task OnExhausted_fires_exactly_once_after_the_final_failed_attempt_with_final_context()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable));
        using var diagnostics = new WebhookDiagnostics();
        var observer = new RecordingExhaustedObserver();
        var options = new WebhookDeliveryOptions { MaxAttempts = 3 };
        var dispatcher = Build(handler, diagnostics, options, observer);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.Equal(1, observer.ExhaustedCalls);
        Assert.Same(result, observer.LastResult);
        Assert.Equal(3, observer.LastResult!.Attempts);
        Assert.Equal(503, observer.LastResult.StatusCode);
        Assert.False(observer.LastResult.Succeeded);
    }

    private sealed class RecordingExhaustedObserver : IWebhookDeliveryObserver
    {
        public int ExhaustedCalls { get; private set; }
        public WebhookDeliveryResult? LastResult { get; private set; }

        public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception)
        {
        }

        public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result)
        {
            ExhaustedCalls++;
            LastResult = result;
        }
    }
}
