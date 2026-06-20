namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;

using Xunit;

/// <summary>
/// Coverage for the telemetry surface: the counters and histogram exposed on the OrionRelay meter,
/// observed end to end through a <see cref="MetricCollector"/> (a real <c>MeterListener</c>). The
/// collector is scoped to the specific diagnostics instance so parallel test classes that create
/// their own same-named meter do not bleed measurements in.
/// </summary>
public sealed class WebhookDiagnosticsTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static WebhookMessage Message(string? eventType = "ping") => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{}"),
        EventType = eventType,
    };

    private static WebhookDispatcher Build(
        StubHttpMessageHandler handler, WebhookDiagnostics diagnostics, WebhookDeliveryOptions? options = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? new WebhookDeliveryOptions(),
            diagnostics,
            signer: null,
            observer: null,
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.0,
            now: () => FixedNow);

    [Fact]
    public void Meter_name_and_instrument_names_are_the_published_contract()
    {
        Assert.Equal("Moongazing.OrionRelay", WebhookDiagnostics.MeterName);

        using var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);

        diagnostics.Delivered.Add(1);
        diagnostics.Attempts.Add(1);
        diagnostics.AttemptsPerDelivery.Record(1);

        var names = collector.Measurements.Select(m => m.Instrument).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("orionrelay.deliveries", names);
        Assert.Contains("orionrelay.attempts", names);
        Assert.Contains("orionrelay.delivery.attempts", names);
    }

    [Fact]
    public void Meter_version_is_derived_from_the_assembly_not_hardcoded()
    {
        using var diagnostics = new WebhookDiagnostics();

        // The meter version self-derives from the package version via MeterVersion, so it must be
        // a non-empty value that matches the resolved assembly informational version rather than a
        // stale literal.
        Assert.False(string.IsNullOrEmpty(MeterVersion.Value));
        Assert.Equal(MeterVersion.Value, diagnostics.MeterVersion);
    }

    [Fact]
    public async Task A_successful_delivery_emits_one_success_attempt_and_one_succeeded_delivery()
    {
        using var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));

        await Build(handler, diagnostics).DispatchAsync(Message());

        var single = Assert.Single(collector.ForInstrument("orionrelay.attempts"));
        Assert.Equal(1d, single.Value);
        Assert.Equal("success", single.Tags["outcome"]);

        var delivered = Assert.Single(collector.ForInstrument("orionrelay.deliveries"));
        Assert.Equal("succeeded", delivered.Tags["outcome"]);
        Assert.Equal("ping", delivered.Tags["event_type"]);

        var perDelivery = Assert.Single(collector.ForInstrument("orionrelay.delivery.attempts"));
        Assert.Equal(1d, perDelivery.Value);
        Assert.Equal("ping", perDelivery.Tags["event_type"]);
    }

    [Fact]
    public async Task A_retry_then_success_tags_one_retryable_then_one_success_attempt()
    {
        using var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));

        await Build(handler, diagnostics).DispatchAsync(Message());

        var outcomes = collector.ForInstrument("orionrelay.attempts").Select(m => m.Tags["outcome"]).ToList();
        Assert.Equal(["retryable", "success"], outcomes);

        var perDelivery = Assert.Single(collector.ForInstrument("orionrelay.delivery.attempts"));
        Assert.Equal(2d, perDelivery.Value);
    }

    [Fact]
    public async Task A_fatal_status_tags_the_attempt_fatal_and_the_delivery_failed()
    {
        using var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.BadRequest));

        await Build(handler, diagnostics).DispatchAsync(Message());

        var attempt = Assert.Single(collector.ForInstrument("orionrelay.attempts"));
        Assert.Equal("fatal", attempt.Tags["outcome"]);

        var delivered = Assert.Single(collector.ForInstrument("orionrelay.deliveries"));
        Assert.Equal("failed", delivered.Tags["outcome"]);
    }

    [Fact]
    public async Task An_exhausted_budget_records_the_full_attempt_count_in_the_histogram()
    {
        using var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));

        await Build(handler, diagnostics, new WebhookDeliveryOptions { MaxAttempts = 3 }).DispatchAsync(Message());

        Assert.Equal(3, collector.ForInstrument("orionrelay.attempts").Count);
        Assert.All(collector.ForInstrument("orionrelay.attempts"), m => Assert.Equal("retryable", m.Tags["outcome"]));

        var perDelivery = Assert.Single(collector.ForInstrument("orionrelay.delivery.attempts"));
        Assert.Equal(3d, perDelivery.Value);

        var delivered = Assert.Single(collector.ForInstrument("orionrelay.deliveries"));
        Assert.Equal("failed", delivered.Tags["outcome"]);
    }

    [Fact]
    public async Task A_message_without_an_event_type_tags_the_none_sentinel()
    {
        using var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));

        await Build(handler, diagnostics).DispatchAsync(Message(eventType: null));

        var delivered = Assert.Single(collector.ForInstrument("orionrelay.deliveries"));
        Assert.Equal("(none)", delivered.Tags["event_type"]);
    }

    [Fact]
    public void Disposing_diagnostics_disposes_the_meter_and_stops_publishing()
    {
        var diagnostics = new WebhookDiagnostics();
        using var collector = new MetricCollector(diagnostics);

        diagnostics.Delivered.Add(1);
        Assert.Single(collector.ForInstrument("orionrelay.deliveries"));

        diagnostics.Dispose();

        // After disposal the instrument is dead; a further add publishes nothing more.
        diagnostics.Delivered.Add(1);
        Assert.Single(collector.ForInstrument("orionrelay.deliveries"));
    }
}
