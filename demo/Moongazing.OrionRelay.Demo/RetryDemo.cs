namespace Moongazing.OrionRelay.Demo;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;
using Moongazing.OrionRelay.Signing;

/// <summary>
/// Drives the real <see cref="WebhookDispatcher"/> against an in-memory stub handler (no network)
/// to show transient-aware retries with exponential backoff, fail-fast on a non-retryable 4xx,
/// and budget exhaustion. The delivery observer prints each attempt; backoff uses a tiny BaseDelay
/// so the retry loop runs in well under a second of real waiting.
/// </summary>
internal static class RetryDemo
{
    public static async Task RunAsync(WebhookDiagnostics diagnostics)
    {
        DemoConsole.Banner("3. Transient-aware retries + equal-jitter backoff + observer hook");

        // Small, real backoff so the loop is fast but still exercises Task.Delay-based waits.
        var options = new WebhookDeliveryOptions
        {
            MaxAttempts = 4,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(80),
            RequestTimeout = TimeSpan.FromSeconds(5),
        };

        await RetryThenSucceed(diagnostics, options);
        await FailFastOn4xx(diagnostics, options);
        await ExhaustBudget(diagnostics, options);
    }

    private static async Task RetryThenSucceed(WebhookDiagnostics diagnostics, WebhookDeliveryOptions options)
    {
        DemoConsole.Section("Two transient failures, then success");
        var handler = new StubHttpMessageHandler(
        [
            StubHttpMessageHandler.Step.Transport("connection reset"),
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.OK),
        ]);

        var observer = new RecordingObserver();
        var result = await Dispatch(handler, options, diagnostics, observer);

        DemoConsole.Item("HTTP attempts made", handler.CallCount.ToString());
        DemoConsole.Item("Result.Succeeded", result.Succeeded.ToString());
        DemoConsole.Item("Result.Attempts", result.Attempts.ToString());
        DemoConsole.Item("Result.StatusCode", result.StatusCode?.ToString() ?? "none");
        DemoConsole.Item("Signed request", handler.LastSignatureHeader is not null ? "yes (Orion-Signature sent)" : "no");
    }

    private static async Task FailFastOn4xx(WebhookDiagnostics diagnostics, WebhookDeliveryOptions options)
    {
        DemoConsole.Section("Non-retryable 400: fail fast, no retries");
        var handler = new StubHttpMessageHandler(
        [
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.BadRequest),
        ]);

        var observer = new RecordingObserver();
        var result = await Dispatch(handler, options, diagnostics, observer);

        DemoConsole.Item("HTTP attempts made", handler.CallCount.ToString());
        DemoConsole.Item("Result.Succeeded", result.Succeeded.ToString());
        DemoConsole.Note("A 400 is permanent, so the dispatcher does not waste the remaining budget.");
    }

    private static async Task ExhaustBudget(WebhookDiagnostics diagnostics, WebhookDeliveryOptions options)
    {
        DemoConsole.Section("Endpoint down for every attempt: exhaust the budget");
        var handler = new StubHttpMessageHandler(
        [
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.ServiceUnavailable),
        ]);

        var observer = new RecordingObserver();
        var result = await Dispatch(handler, options, diagnostics, observer);

        DemoConsole.Item("HTTP attempts made", handler.CallCount.ToString());
        DemoConsole.Item("Result.Succeeded", result.Succeeded.ToString());
        DemoConsole.Item("Result.Attempts", result.Attempts.ToString());
        DemoConsole.Item("observer.OnExhausted fired", (observer.Exhausted == 1).ToString());
    }

    private static async Task<WebhookDeliveryResult> Dispatch(
        StubHttpMessageHandler handler,
        WebhookDeliveryOptions options,
        WebhookDiagnostics diagnostics,
        IWebhookDeliveryObserver observer)
    {
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var dispatcher = new WebhookDispatcher(
            httpClient,
            options,
            diagnostics,
            new WebhookSigner(SigningDemo.Secret),
            observer);

        var message = new WebhookMessage
        {
            Endpoint = new Uri("https://receiver.example.test/hooks"),
            Body = Encoding.UTF8.GetBytes("""{"event":"order.created","id":"ord_1024"}"""),
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "order.created",
        };

        return await dispatcher.DispatchAsync(message);
    }
}
