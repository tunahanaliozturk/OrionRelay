namespace Moongazing.OrionRelay.Tests;

using System.Diagnostics.Metrics;

using Moongazing.OrionRelay.Diagnostics;

/// <summary>
/// Subscribes to the OrionRelay <see cref="Meter"/> via a <see cref="MeterListener"/> and records
/// every measurement published while the collector is alive.
/// <para>
/// Every <see cref="WebhookDiagnostics"/> creates a meter with the same name, and xUnit runs test
/// classes in parallel, so filtering by meter name alone would capture measurements from unrelated
/// dispatchers running concurrently. Pass the diagnostics instance under test to scope the
/// collector to that meter's instruments by reference, keeping each test isolated.
/// </para>
/// </summary>
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener listener;
    private readonly List<Measurement> measurements = [];
    private readonly object gate = new();
    private readonly HashSet<Instrument> tracked = [];

    /// <param name="scope">
    /// When supplied, only instruments belonging to this diagnostics instance's meter are recorded.
    /// When null, every instrument on the OrionRelay meter (by name) is recorded.
    /// </param>
    public MetricCollector(WebhookDiagnostics? scope = null)
    {
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name != WebhookDiagnostics.MeterName)
                {
                    return;
                }

                if (scope is not null && !BelongsTo(instrument, scope))
                {
                    return;
                }

                lock (gate)
                {
                    tracked.Add(instrument);
                }

                l.EnableMeasurementEvents(instrument);
            },
        };

        listener.SetMeasurementEventCallback<long>(OnLong);
        listener.SetMeasurementEventCallback<int>(OnInt);
        listener.Start();
    }

    /// <summary>A captured measurement: the instrument name, the value, and the tags.</summary>
    internal readonly record struct Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (gate)
            {
                return [.. measurements];
            }
        }
    }

    /// <summary>All measurements recorded for a given instrument name.</summary>
    public IReadOnlyList<Measurement> ForInstrument(string instrument) =>
        [.. Measurements.Where(m => m.Instrument == instrument)];

    public void Dispose() => listener.Dispose();

    private static bool BelongsTo(Instrument instrument, WebhookDiagnostics scope) =>
        ReferenceEquals(instrument, scope.Delivered)
        || ReferenceEquals(instrument, scope.Attempts)
        || ReferenceEquals(instrument, scope.AttemptsPerDelivery);

    private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        Add(instrument, value, tags);

    private void OnInt(Instrument instrument, int value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
        Add(instrument, value, tags);

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        lock (gate)
        {
            measurements.Add(new Measurement(instrument.Name, value, map));
        }
    }
}
