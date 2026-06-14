namespace Moongazing.OrionRelay.Observers;

using Moongazing.OrionRelay.Delivery;

/// <summary>
/// Consumer hook notified about delivery lifecycle events. Implementations are for observability
/// only: they must not throw, and the dispatcher swallows any fault they raise so an observer
/// outage can never break webhook delivery. Register one via DI, or leave it unset for a no-op.
/// </summary>
public interface IWebhookDeliveryObserver
{
    /// <summary>
    /// Called after each individual HTTP attempt, whether it succeeded or failed.
    /// </summary>
    /// <param name="message">The message being delivered.</param>
    /// <param name="attempt">The 1-based attempt number.</param>
    /// <param name="statusCode">The HTTP status received, or null on a transport fault.</param>
    /// <param name="exception">The transport fault, when the attempt produced no response.</param>
    void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception);

    /// <summary>
    /// Called once when a delivery is abandoned after exhausting its attempt budget.
    /// </summary>
    /// <param name="message">The message that could not be delivered.</param>
    /// <param name="result">The terminal failure result.</param>
    void OnExhausted(WebhookMessage message, WebhookDeliveryResult result);
}

/// <summary>A no-op observer used when the consumer registers none.</summary>
public sealed class NullWebhookDeliveryObserver : IWebhookDeliveryObserver
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NullWebhookDeliveryObserver Instance = new();

    private NullWebhookDeliveryObserver()
    {
    }

    /// <inheritdoc />
    public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception)
    {
    }

    /// <inheritdoc />
    public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result)
    {
    }
}
