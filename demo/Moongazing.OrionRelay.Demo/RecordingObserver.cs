namespace Moongazing.OrionRelay.Demo;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Observers;

/// <summary>
/// A delivery observer that prints every attempt and every exhausted delivery, as a real
/// dead-letter or alerting hook would. Demonstrates the <see cref="IWebhookDeliveryObserver"/>
/// contract; the dispatcher swallows any fault it raises so it is observability only.
/// </summary>
internal sealed class RecordingObserver : IWebhookDeliveryObserver
{
    public int Attempts { get; private set; }

    public int Exhausted { get; private set; }

    public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception)
    {
        Attempts++;
        var outcome = exception is not null
            ? $"transport fault ({exception.Message})"
            : $"HTTP {statusCode}";
        DemoConsole.Note($"observer.OnAttempt -> attempt #{attempt}: {outcome}");
    }

    public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result)
    {
        Exhausted++;
        DemoConsole.Note(
            $"observer.OnExhausted -> budget exhausted after {result.Attempts} attempts " +
            $"(last status: {(result.StatusCode?.ToString() ?? "none")}). Park for redelivery.");
    }
}
