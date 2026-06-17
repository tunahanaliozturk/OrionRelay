namespace Moongazing.OrionRelay.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionRelay.Diagnostics;

/// <summary>
/// Measures the per-attempt telemetry emission the dispatcher performs against
/// <see cref="WebhookDiagnostics"/>: incrementing the attempt counter with an <c>outcome</c> tag,
/// the delivery counter with two tags, and recording the attempts-per-delivery histogram. This
/// isolates the metric-instrument and tag-handling overhead (including the boxing of tag values
/// into <see cref="KeyValuePair{TKey,TValue}"/>) on the delivery path. No listener is attached, so
/// this reflects the unsubscribed cost paid on every send. No network or I/O is involved.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class TelemetryBenchmarks
{
    private WebhookDiagnostics diagnostics = null!;

    [GlobalSetup]
    public void Setup() => diagnostics = new WebhookDiagnostics();

    [GlobalCleanup]
    public void Cleanup() => diagnostics.Dispose();

    /// <summary>Emit the attempt counter with the per-attempt outcome tag.</summary>
    [Benchmark]
    public void RecordAttempt() =>
        diagnostics.Attempts.Add(1, new KeyValuePair<string, object?>("outcome", "retryable"));

    /// <summary>Emit the full completion telemetry: delivery counter (two tags) plus the histogram.</summary>
    [Benchmark]
    public void RecordCompletion()
    {
        var eventTypeTag = new KeyValuePair<string, object?>("event_type", "order.created");
        diagnostics.Delivered.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "succeeded"),
            eventTypeTag);
        diagnostics.AttemptsPerDelivery.Record(3, eventTypeTag);
    }
}
