namespace Moongazing.OrionRelay.Demo;

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Signing;

/// <summary>
/// Subscribes a <see cref="MeterListener"/> to the <c>Moongazing.OrionRelay</c> meter and runs a
/// few in-memory dispatches so the per-attempt and per-delivery instruments emit. Aggregates the
/// measurements by instrument and tag, then prints the tallies the way an OpenTelemetry exporter
/// would surface them.
/// </summary>
internal static class TelemetryDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Banner("4. Per-attempt telemetry via MeterListener");

        var counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var histogramSamples = new List<int>();

        // A dedicated diagnostics instance so this demo's listener only sees its own traffic.
        using var diagnostics = new WebhookDiagnostics();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WebhookDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var key = $"{instrument.Name} {{{FormatTags(tags)}}}";
            counters.TryGetValue(key, out var current);
            counters[key] = current + value;
        });

        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "orionrelay.delivery.attempts")
            {
                histogramSamples.Add(value);
            }
        });

        listener.Start();

        await DriveTraffic(diagnostics);

        // Flush any buffered observable measurements (none here, but correct form).
        listener.RecordObservableInstruments();

        DemoConsole.Section("Counters (meter: " + WebhookDiagnostics.MeterName + ")");
        foreach (var (key, total) in counters)
        {
            DemoConsole.Item(key, total.ToString(CultureInfo.InvariantCulture));
        }

        DemoConsole.Section("Histogram orionrelay.delivery.attempts");
        DemoConsole.Item("Samples (attempts/delivery)", string.Join(", ", histogramSamples));
        if (histogramSamples.Count > 0)
        {
            DemoConsole.Item("Total deliveries observed", histogramSamples.Count.ToString(CultureInfo.InvariantCulture));
            DemoConsole.Item("Mean attempts/delivery", histogramSamples.Average().ToString("0.00", CultureInfo.InvariantCulture));
        }
    }

    private static async Task DriveTraffic(WebhookDiagnostics diagnostics)
    {
        var options = new WebhookDeliveryOptions
        {
            MaxAttempts = 4,
            BaseDelay = TimeSpan.FromMilliseconds(10),
            MaxDelay = TimeSpan.FromMilliseconds(40),
            RequestTimeout = TimeSpan.FromSeconds(5),
        };

        // A clean first-attempt success.
        await Dispatch(diagnostics, options,
        [
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.OK),
        ]);

        // One retry then success (two attempts).
        await Dispatch(diagnostics, options,
        [
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.OK),
        ]);

        // A delivery that exhausts the budget (all 5xx).
        await Dispatch(diagnostics, options,
        [
            StubHttpMessageHandler.Step.Responds(HttpStatusCode.BadGateway),
        ]);
    }

    private static async Task Dispatch(
        WebhookDiagnostics diagnostics,
        WebhookDeliveryOptions options,
        IEnumerable<StubHttpMessageHandler.Step> script)
    {
        var handler = new StubHttpMessageHandler(script);
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var dispatcher = new WebhookDispatcher(
            httpClient, options, diagnostics, new WebhookSigner(SigningDemo.Secret), observer: null);

        await dispatcher.DispatchAsync(new WebhookMessage
        {
            Endpoint = new Uri("https://receiver.example.test/hooks"),
            Body = Encoding.UTF8.GetBytes("""{"event":"order.created"}"""),
            EventType = "order.created",
        });
    }

    private static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < tags.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(tags[i].Key).Append('=').Append(tags[i].Value);
        }
        return sb.ToString();
    }
}
