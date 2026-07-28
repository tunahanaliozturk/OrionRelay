namespace Moongazing.OrionRelay.Diagnostics;

using System.Diagnostics.Metrics;

using Moongazing.Orion.Abstractions.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for webhook delivery. Built on the Orion family's
/// <see cref="OrionInstrumentation"/> spine, so it shares the family's naming and static-tag
/// conventions: a <see cref="Meter"/> named <c>Moongazing.OrionRelay</c> (subscribe by that name)
/// carrying delivery counters <c>orion.relay.deliveries</c> / <c>orion.relay.attempts</c> and the
/// attempt-count histogram <c>orion.relay.delivery.attempts</c>. Multi-tenant / multi-region labels
/// configured through <see cref="OrionInstrumentation.SetStaticTags"/> are stamped onto every
/// measurement recorded through the <c>Record*</c> methods. One instance is registered as a
/// singleton; dispose it to release the meter.
/// </summary>
public sealed class WebhookDiagnostics : OrionInstrumentation
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionRelay";

    /// <summary>Create the meter and its instruments.</summary>
    public WebhookDiagnostics()
        : base(OrionTelemetry.ScopeName("OrionRelay"), MeterVersion.Value)
    {
        Delivered = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("relay", "deliveries"),
            unit: "{delivery}",
            description: "Webhook deliveries that completed, tagged outcome (succeeded/failed) and event_type.");

        Attempts = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("relay", "attempts"),
            unit: "{attempt}",
            description: "Individual HTTP attempts made, tagged outcome (success/retryable/fatal).");

        AttemptsPerDelivery = Meter.CreateHistogram<int>(
            OrionTelemetry.MetricName("relay", "delivery.attempts"),
            unit: "{attempt}",
            description: "Number of attempts a delivery took before it succeeded or was abandoned.");
    }

    /// <summary>Counts completed deliveries (one per <c>DispatchAsync</c> call).</summary>
    public Counter<long> Delivered { get; }

    /// <summary>Counts individual HTTP attempts.</summary>
    public Counter<long> Attempts { get; }

    /// <summary>Records the attempt count of each completed delivery.</summary>
    public Histogram<int> AttemptsPerDelivery { get; }

    /// <summary>The version stamped on the underlying meter, derived from the assembly version.</summary>
    internal string? MeterVersionValue => Meter.Version;

    /// <summary>Record one HTTP attempt, tagged with its outcome.</summary>
    /// <param name="outcome">The attempt outcome (<c>success</c> / <c>retryable</c> / <c>fatal</c>).</param>
    public void RecordAttempt(string outcome) =>
        Attempts.Add(1, Tag(new KeyValuePair<string, object?>(OrionTelemetry.Tags.Outcome, outcome)));

    /// <summary>Record a completed delivery: the outcome counter and the attempts-per-delivery histogram.</summary>
    /// <param name="succeeded">Whether the delivery ultimately succeeded.</param>
    /// <param name="attempts">The number of attempts the delivery took.</param>
    /// <param name="eventTypeTag">The <c>event_type</c> tag for this message.</param>
    public void RecordDelivery(bool succeeded, int attempts, KeyValuePair<string, object?> eventTypeTag)
    {
        var outcomeTag = new KeyValuePair<string, object?>(
            OrionTelemetry.Tags.Outcome, succeeded ? "succeeded" : "failed");
        Delivered.Add(1, Compose(outcomeTag, eventTypeTag));
        AttemptsPerDelivery.Record(attempts, Compose(eventTypeTag));
    }

    // Merge the configured static tags with this measurement's domain tags. The base Tag() helper
    // covers the single-extra case; deliveries carry two domain tags, so build the array here.
    // Allocation-free passthrough when no static tags are configured (the common case).
    private KeyValuePair<string, object?>[] Compose(params KeyValuePair<string, object?>[] extra)
    {
        if (StaticTags.Length == 0)
        {
            return extra;
        }

        var all = new KeyValuePair<string, object?>[StaticTags.Length + extra.Length];
        StaticTags.CopyTo(all, 0);
        extra.CopyTo(all, StaticTags.Length);
        return all;
    }
}
