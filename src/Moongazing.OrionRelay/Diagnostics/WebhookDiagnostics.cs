namespace Moongazing.OrionRelay.Diagnostics;

using System.Diagnostics.Metrics;

/// <summary>
/// OpenTelemetry instrumentation for webhook delivery. Exposes a <see cref="Meter"/> named
/// <c>Moongazing.OrionRelay</c> carrying delivery counters and an attempt-count histogram.
/// One instance is registered as a singleton; dispose it to release the meter.
/// </summary>
public sealed class WebhookDiagnostics : IDisposable
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionRelay";

    private readonly Meter meter;

    /// <summary>Create the meter and its instruments.</summary>
    public WebhookDiagnostics()
    {
        meter = new Meter(MeterName, "0.1.0");

        Delivered = meter.CreateCounter<long>(
            "orionrelay.deliveries",
            unit: "{delivery}",
            description: "Webhook deliveries that completed, tagged outcome (succeeded/failed) and event_type.");

        Attempts = meter.CreateCounter<long>(
            "orionrelay.attempts",
            unit: "{attempt}",
            description: "Individual HTTP attempts made, tagged outcome (success/retryable/fatal).");

        AttemptsPerDelivery = meter.CreateHistogram<int>(
            "orionrelay.delivery.attempts",
            unit: "{attempt}",
            description: "Number of attempts a delivery took before it succeeded or was abandoned.");
    }

    /// <summary>Counts completed deliveries (one per <c>DispatchAsync</c> call).</summary>
    public Counter<long> Delivered { get; }

    /// <summary>Counts individual HTTP attempts.</summary>
    public Counter<long> Attempts { get; }

    /// <summary>Records the attempt count of each completed delivery.</summary>
    public Histogram<int> AttemptsPerDelivery { get; }

    /// <inheritdoc />
    public void Dispose() => meter.Dispose();
}
