namespace Moongazing.OrionRelay.Demo;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Signing;

/// <summary>
/// Drives the real <see cref="WebhookDispatcher"/> against an always-down in-memory stub (no
/// network) so a delivery exhausts its attempt budget, then shows the abandoned delivery being
/// routed to a bounded <see cref="InMemoryDeadLetterSink"/> exactly once, carrying its terminal
/// failure context. Backoff uses a tiny BaseDelay so the retry loop runs in well under a second of
/// real waiting.
/// </summary>
internal static class DeadLetterDemo
{
    public static async Task RunAsync(WebhookDiagnostics diagnostics)
    {
        DemoConsole.Banner("5. Dead-letter sink: capture deliveries that exhaust their budget");

        var options = new WebhookDeliveryOptions
        {
            MaxAttempts = 3,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(80),
            RequestTimeout = TimeSpan.FromSeconds(5),
        };

        // Bounded, opt-in in-memory sink: retain the most recent abandoned deliveries, oldest-first
        // eviction once full. A durable store would take its place in production.
        var sink = new InMemoryDeadLetterSink(capacity: 16);

        DemoConsole.Section("Endpoint down for every attempt: exhaust the budget, then dead-letter");
        var handler = new StubHttpMessageHandler(
        [
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.ServiceUnavailable),
        ]);

        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var dispatcher = new WebhookDispatcher(
            httpClient,
            options,
            diagnostics,
            new WebhookSigner(SigningDemo.Secret),
            observer: null,
            deadLetterSink: sink);

        var message = new WebhookMessage
        {
            Endpoint = new Uri("https://receiver.example.test/hooks"),
            Body = Encoding.UTF8.GetBytes("""{"event":"order.created","id":"ord_2048"}"""),
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "order.created",
        };

        var result = await dispatcher.DispatchAsync(message);

        DemoConsole.Item("HTTP attempts made", handler.CallCount.ToString());
        DemoConsole.Item("Result.Succeeded", result.Succeeded.ToString());
        DemoConsole.Item("sink.Count", sink.Count.ToString());

        // The sink received the abandoned delivery exactly once, with its terminal failure context.
        foreach (var entry in sink.Entries)
        {
            DemoConsole.Note(
                $"dead-lettered {entry.Message.EventType} at {entry.DeadLetteredAt:o} " +
                $"after {entry.Result.Attempts} attempts " +
                $"(last status: {entry.Result.StatusCode?.ToString() ?? "none"}). Persist or replay.");
        }

        DemoConsole.Note("The default sink is a no-op; this demo opts in to the bounded in-memory sink.");
    }
}
