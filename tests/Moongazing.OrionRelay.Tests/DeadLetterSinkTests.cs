namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;

using Xunit;

public sealed class DeadLetterSinkTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static WebhookMessage Message() => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{\"event\":\"ping\"}"),
        EventId = "evt_1",
        EventType = "ping",
    };

    private static WebhookDispatcher Build(
        StubHttpMessageHandler handler,
        WebhookDiagnostics diagnostics,
        IDeadLetterSink? deadLetterSink,
        WebhookDeliveryOptions? options = null,
        IWebhookDeliveryObserver? observer = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? new WebhookDeliveryOptions(),
            diagnostics,
            signer: null,
            observer: observer,
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.0,
            now: () => FixedNow,
            deadLetterSink: deadLetterSink);

    [Fact]
    public async Task Exhausted_delivery_lands_in_the_sink_exactly_once_with_final_attempt_info()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        using var diagnostics = new WebhookDiagnostics();
        var sink = new InMemoryDeadLetterSink();
        var options = new WebhookDeliveryOptions { MaxAttempts = 3 };
        var dispatcher = Build(handler, diagnostics, sink, options);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(1, sink.Count);

        var entry = Assert.Single(sink.Entries);
        Assert.Same(result, entry.Result);
        Assert.Equal(3, entry.Result.Attempts);
        Assert.Equal(500, entry.Result.StatusCode);
        Assert.Equal(FixedNow, entry.DeadLetteredAt);
        Assert.Equal("evt_1", entry.Message.EventId);
    }

    [Fact]
    public async Task A_successful_delivery_is_not_dead_lettered()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        using var diagnostics = new WebhookDiagnostics();
        var sink = new InMemoryDeadLetterSink();
        var dispatcher = Build(handler, diagnostics, sink);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task A_non_retryable_failure_is_dead_lettered_once()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.BadRequest));
        using var diagnostics = new WebhookDiagnostics();
        var sink = new InMemoryDeadLetterSink();
        var dispatcher = Build(handler, diagnostics, sink);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        var entry = Assert.Single(sink.Entries);
        Assert.Equal(1, entry.Result.Attempts);
        Assert.Equal(400, entry.Result.StatusCode);
    }

    [Fact]
    public async Task A_faulting_sink_does_not_break_delivery()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        using var diagnostics = new WebhookDiagnostics();
        var options = new WebhookDeliveryOptions { MaxAttempts = 2 };
        var dispatcher = Build(handler, diagnostics, new ThrowingDeadLetterSink(), options);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task InMemory_sink_preserves_arrival_order()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        using var diagnostics = new WebhookDiagnostics();
        var sink = new InMemoryDeadLetterSink();
        var options = new WebhookDeliveryOptions { MaxAttempts = 1 };
        var dispatcher = Build(handler, diagnostics, sink, options);

        await dispatcher.DispatchAsync(new WebhookMessage
        {
            Endpoint = new Uri("https://example.test/hook"),
            Body = Encoding.UTF8.GetBytes("{}"),
            EventId = "first",
        });
        await dispatcher.DispatchAsync(new WebhookMessage
        {
            Endpoint = new Uri("https://example.test/hook"),
            Body = Encoding.UTF8.GetBytes("{}"),
            EventId = "second",
        });

        Assert.Equal(2, sink.Count);
        Assert.Equal("first", sink.Entries[0].Message.EventId);
        Assert.Equal("second", sink.Entries[1].Message.EventId);
    }

    [Fact]
    public async Task Restored_five_argument_ctor_routes_an_exhausted_delivery_to_a_no_op_without_throwing()
    {
        // The v0.1.0 public ABI: (HttpClient, options, diagnostics, signer?, observer?). It must
        // remain a real overload so a 0.1.0-compiled consumer binds at runtime, and an exhausted
        // delivery through it must fall back to a no-op sink rather than throw.
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.BadRequest));
        using var diagnostics = new WebhookDiagnostics();
        var dispatcher = new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new WebhookDeliveryOptions { MaxAttempts = 1 },
            diagnostics,
            signer: null,
            observer: null);

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task InMemory_sink_evicts_the_oldest_entry_beyond_capacity()
    {
        // A bounded sink must not grow without limit: writing past Capacity drops oldest-first.
        var sink = new InMemoryDeadLetterSink(capacity: 2);
        var failure = WebhookDeliveryResult.Failure(1, 500, null);

        await sink.WriteAsync(new DeadLetterEntry(Message("first"), failure, FixedNow));
        await sink.WriteAsync(new DeadLetterEntry(Message("second"), failure, FixedNow));
        await sink.WriteAsync(new DeadLetterEntry(Message("third"), failure, FixedNow));

        Assert.Equal(2, sink.Count);
        Assert.Equal(2, sink.Capacity);
        Assert.Equal("second", sink.Entries[0].Message.EventId);
        Assert.Equal("third", sink.Entries[1].Message.EventId);
    }

    [Fact]
    public void InMemory_sink_rejects_a_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryDeadLetterSink(capacity: 0));
    }

    private static WebhookMessage Message(string eventId) => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{\"event\":\"ping\"}"),
        EventId = eventId,
        EventType = "ping",
    };

    private sealed class ThrowingDeadLetterSink : IDeadLetterSink
    {
        public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sink boom");
    }
}
