namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// A delivery that exhausted its retry budget, captured for a <see cref="IDeadLetterSink"/>: the
/// original message paired with the terminal failure context and the moment it was abandoned.
/// </summary>
public sealed class DeadLetterEntry
{
    /// <summary>Create an entry.</summary>
    /// <param name="message">The message that could not be delivered.</param>
    /// <param name="result">The terminal failure result for the final attempt.</param>
    /// <param name="deadLetteredAt">When the delivery was abandoned.</param>
    public DeadLetterEntry(WebhookMessage message, WebhookDeliveryResult result, DateTimeOffset deadLetteredAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(result);

        Message = message;
        Result = result;
        DeadLetteredAt = deadLetteredAt;
    }

    /// <summary>The message that could not be delivered within its attempt budget.</summary>
    public WebhookMessage Message { get; }

    /// <summary>
    /// The terminal failure result: the number of attempts made, the last HTTP status observed,
    /// and the final transport fault if delivery ended on one.
    /// </summary>
    public WebhookDeliveryResult Result { get; }

    /// <summary>The instant the delivery was abandoned and routed to the sink.</summary>
    public DateTimeOffset DeadLetteredAt { get; }
}
