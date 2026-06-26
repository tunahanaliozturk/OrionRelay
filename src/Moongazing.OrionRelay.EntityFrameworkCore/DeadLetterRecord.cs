namespace Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// The persisted row backing one abandoned webhook delivery in
/// <see cref="EntityFrameworkCoreDeadLetterSink{TContext}"/>. It carries everything the
/// <c>DeadLetterEntry</c> handed to the sink holds: where the delivery was bound, the payload and
/// the headers a receiver would have seen, how many attempts were made, the terminal failure, and
/// when the delivery was abandoned. A held delivery is read back as a row of this shape for
/// inspection, triage, or a later replay.
/// </summary>
public sealed class DeadLetterRecord
{
    /// <summary>
    /// The stable identity of the abandoned delivery. Primary key, so the database's primary-key
    /// uniqueness is what makes the write idempotent: re-routing the same terminal delivery a second
    /// time updates the existing row in place rather than inserting a duplicate. It is the delivery's
    /// <c>EventId</c> when the message carried one; otherwise a per-write surrogate is assigned, since
    /// an unidentified delivery has nothing stable to deduplicate on and each abandonment is a
    /// distinct event.
    /// </summary>
    public required string DeliveryId { get; set; }

    /// <summary>The absolute endpoint the delivery was bound to (the message's target URL).</summary>
    public required string Endpoint { get; set; }

    /// <summary>The request payload bytes, stored verbatim as the receiver would have seen them.</summary>
    public required byte[] Body { get; set; }

    /// <summary>The body media type sent with the payload (for example <c>application/json</c>).</summary>
    public required string ContentType { get; set; }

    /// <summary>
    /// The logical event type carried on the <c>Orion-Event-Type</c> header, or null when the message
    /// did not set one. Retained so triage and replay can filter by event type.
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// The <c>Orion-Event-Id</c> header value the delivery carried, or null when the message did not
    /// set one. Distinct from <see cref="DeliveryId"/>, which falls back to a surrogate so the row
    /// always has a key; this column preserves the original header value exactly as sent.
    /// </summary>
    public string? EventId { get; set; }

    /// <summary>The number of delivery attempts made before the budget was exhausted.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// The HTTP status code of the last response observed, or null when every attempt failed at the
    /// transport level before a response was produced.
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// The final error: the transport exception's message from the last attempt when delivery ended
    /// on a transport fault, or null when it ended on an HTTP error status (which
    /// <see cref="StatusCode"/> then records). The message text is captured rather than the exception
    /// instance, since the row must survive a process restart and an exception does not round-trip
    /// through storage.
    /// </summary>
    public string? FinalError { get; set; }

    /// <summary>
    /// The instant the delivery was abandoned and routed to the sink, as UTC ticks
    /// (<see cref="DateTimeOffset.UtcTicks"/>). Stored as a primitive integer rather than a
    /// <see cref="DateTimeOffset"/> so ordering and age filters translate to a plain integer predicate
    /// on every relational provider, free of provider-specific date handling.
    /// </summary>
    public long DeadLetteredAtTicks { get; set; }
}
