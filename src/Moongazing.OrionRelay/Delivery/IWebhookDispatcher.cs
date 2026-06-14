namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// Delivers a webhook to its endpoint, signing the request and retrying transient failures with
/// exponential backoff until it succeeds or the attempt budget is exhausted.
/// </summary>
public interface IWebhookDispatcher
{
    /// <summary>
    /// Deliver a single message. Returns when delivery succeeds (a 2xx response) or the attempt
    /// budget is exhausted; the returned result reports which. A cancelled
    /// <paramref name="cancellationToken"/> aborts delivery and throws
    /// <see cref="OperationCanceledException"/> rather than returning a failure result.
    /// </summary>
    /// <param name="message">The webhook to deliver.</param>
    /// <param name="cancellationToken">Cancels the whole delivery, including backoff waits.</param>
    Task<WebhookDeliveryResult> DispatchAsync(WebhookMessage message, CancellationToken cancellationToken = default);
}
