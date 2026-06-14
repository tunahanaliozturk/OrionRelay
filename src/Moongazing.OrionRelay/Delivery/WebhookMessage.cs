namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// A single webhook to deliver: where to POST, what to send, and the metadata a receiver
/// uses for routing and idempotency.
/// </summary>
public sealed class WebhookMessage
{
    /// <summary>The absolute endpoint to POST to.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The request body bytes, transmitted verbatim and covered by the signature.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>The body media type. Defaults to <c>application/json</c>.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>
    /// A stable identifier for this event, sent as the <c>Orion-Event-Id</c> header so a
    /// receiver can deduplicate redelivered events. Optional.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// The logical event type (for example <c>order.created</c>), sent as the
    /// <c>Orion-Event-Type</c> header. Optional, used for receiver-side routing and for the
    /// <c>event_type</c> telemetry tag.
    /// </summary>
    public string? EventType { get; init; }
}
