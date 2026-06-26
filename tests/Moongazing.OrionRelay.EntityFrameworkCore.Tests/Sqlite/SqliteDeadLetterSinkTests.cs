namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests.Sqlite;

using System.Text;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// Integration tests for <see cref="EntityFrameworkCoreDeadLetterSink{TContext}"/> over a real
/// file-based SQLite database, so the durable sink is exercised against genuine relational
/// constraints, transactions, and the primary-key enforcement the idempotent re-route relies on.
/// </summary>
public sealed class SqliteDeadLetterSinkTests
{
    private static readonly DateTimeOffset AbandonedAt =
        new(2026, 6, 27, 12, 30, 0, TimeSpan.Zero);

    private static readonly string[] NewestFirstAll = { "evt-new", "evt-mid", "evt-old" };
    private static readonly string[] NewestFirstTop2 = { "evt-new", "evt-mid" };

    [Fact]
    public async Task abandoned_delivery_is_persisted_to_the_durable_sink()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        await sink.WriteAsync(Entry("evt-1", "https://receiver.test/hook", "{\"ok\":true}"));

        var held = await sink.GetHeldAsync();
        var record = Assert.Single(held);
        Assert.Equal("evt-1", record.DeliveryId);
        Assert.Equal("https://receiver.test/hook", record.Endpoint);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(record.Body));
    }

    [Fact]
    public async Task persisted_delivery_is_readable_after_a_fresh_dbcontext_simulating_restart()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();

        // Write through one sink, then drop every context to the file and read back through a brand
        // new sink built over the same database. Nothing is held in memory across the two, so a hit
        // here can only come from the persisted row: the restart-survival guarantee.
        var writer = harness.CreateSink();
        await writer.WriteAsync(Entry("evt-restart", "https://receiver.test/a", "payload"));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var afterRestart = harness.CreateSink();
        var held = await afterRestart.GetHeldAsync();

        var record = Assert.Single(held);
        Assert.Equal("evt-restart", record.DeliveryId);
        Assert.Equal("payload", Encoding.UTF8.GetString(record.Body));
    }

    [Fact]
    public async Task re_routing_the_same_terminal_delivery_lands_it_once()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        // The dispatcher can re-route a replayed terminal delivery; the same EventId keys the row, so
        // the second and third writes must update in place, not accumulate duplicates.
        await sink.WriteAsync(Entry("evt-dup", "https://receiver.test/x", "v1", attempts: 3));
        await sink.WriteAsync(Entry("evt-dup", "https://receiver.test/x", "v2", attempts: 4));
        await sink.WriteAsync(Entry("evt-dup", "https://receiver.test/x", "v3", attempts: 5));

        Assert.Equal(1, await sink.CountAsync());

        var record = Assert.Single(await sink.GetHeldAsync());
        // Last write wins: the parked copy reflects the most recent abandonment.
        Assert.Equal("v3", Encoding.UTF8.GetString(record.Body));
        Assert.Equal(5, record.Attempts);
    }

    [Fact]
    public async Task concurrent_re_route_of_the_same_id_still_lands_once()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();

        // Independent sinks (independent contexts) racing the same id: the primary key admits one row;
        // the loser's DbUpdateException is reconciled to a no-op, not rethrown.
        var writers = Enumerable.Range(0, 8)
            .Select(i => harness.CreateSink().WriteAsync(
                Entry("evt-race", "https://receiver.test/race", $"body-{i}")));

        await Task.WhenAll(writers);

        Assert.Equal(1, await harness.CreateSink().CountAsync());
    }

    [Fact]
    public async Task inspection_query_returns_the_held_deliveries_newest_first()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        await sink.WriteAsync(Entry("evt-old", "https://receiver.test/1", "a", at: AbandonedAt));
        await sink.WriteAsync(Entry("evt-mid", "https://receiver.test/2", "b", at: AbandonedAt.AddMinutes(5)));
        await sink.WriteAsync(Entry("evt-new", "https://receiver.test/3", "c", at: AbandonedAt.AddMinutes(10)));

        var held = await sink.GetHeldAsync();

        Assert.Equal(3, held.Count);
        Assert.Equal(NewestFirstAll, held.Select(r => r.DeliveryId).ToArray());

        // The limit caps the result while preserving newest-first ordering.
        var capped = await sink.GetHeldAsync(limit: 2);
        Assert.Equal(NewestFirstTop2, capped.Select(r => r.DeliveryId).ToArray());
    }

    [Fact]
    public async Task full_delivery_context_is_persisted()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        var message = new WebhookMessage
        {
            Endpoint = new Uri("https://receiver.test/full"),
            Body = Encoding.UTF8.GetBytes("{\"id\":7}"),
            ContentType = "application/json",
            EventId = "evt-full",
            EventType = "order.created",
        };
        var result = WebhookDeliveryResult.Failure(attempts: 4, statusCode: 503, finalException: null);
        await sink.WriteAsync(new DeadLetterEntry(message, result, AbandonedAt));

        var record = Assert.Single(await sink.GetHeldAsync());
        Assert.Equal("evt-full", record.DeliveryId);
        Assert.Equal("evt-full", record.EventId);
        Assert.Equal("order.created", record.EventType);
        Assert.Equal("https://receiver.test/full", record.Endpoint);
        Assert.Equal("application/json", record.ContentType);
        Assert.Equal("{\"id\":7}", Encoding.UTF8.GetString(record.Body));
        Assert.Equal(4, record.Attempts);
        Assert.Equal(503, record.StatusCode);
        Assert.Null(record.FinalError);
        Assert.Equal(AbandonedAt.UtcTicks, record.DeadLetteredAtTicks);
    }

    [Fact]
    public async Task transport_failure_message_is_captured_as_the_final_error()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        var message = new WebhookMessage
        {
            Endpoint = new Uri("https://receiver.test/down"),
            Body = Encoding.UTF8.GetBytes("x"),
            EventId = "evt-transport",
        };
        var fault = new HttpRequestException("connection refused");
        var result = WebhookDeliveryResult.Failure(attempts: 5, statusCode: null, finalException: fault);
        await sink.WriteAsync(new DeadLetterEntry(message, result, AbandonedAt));

        var record = Assert.Single(await sink.GetHeldAsync());
        Assert.Null(record.StatusCode);
        Assert.Equal("connection refused", record.FinalError);
    }

    [Fact]
    public async Task a_delivery_without_an_event_id_is_keyed_by_a_surrogate_so_each_abandonment_is_held()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        // No EventId: nothing stable to deduplicate on, so each abandonment is a distinct held row
        // under its own surrogate key rather than collapsing into one.
        await sink.WriteAsync(Entry(eventId: null, "https://receiver.test/anon", "first"));
        await sink.WriteAsync(Entry(eventId: null, "https://receiver.test/anon", "second"));

        var held = await sink.GetHeldAsync();
        Assert.Equal(2, held.Count);
        Assert.All(held, r => Assert.Null(r.EventId));
        Assert.Equal(2, held.Select(r => r.DeliveryId).Distinct().Count());
    }

    [Fact]
    public async Task held_record_round_trips_through_the_context_dbset()
    {
        await using var harness = await SqliteSinkHarness.CreateAsync();
        var sink = harness.CreateSink();

        await sink.WriteAsync(Entry("evt-dbset", "https://receiver.test/dbset", "z"));

        // Read through the context's own DbSet (not the sink's query) to confirm the row is mapped to
        // the configured table and visible to a plain EF query, as an operator's own tooling would see.
        await using var context = await harness.Factory.CreateDbContextAsync();
        var record = await context.DeadLetters.SingleAsync(r => r.DeliveryId == "evt-dbset");
        Assert.Equal("z", Encoding.UTF8.GetString(record.Body));
    }

    private static DeadLetterEntry Entry(
        string? eventId,
        string endpoint,
        string body,
        int attempts = 1,
        DateTimeOffset? at = null)
    {
        var message = new WebhookMessage
        {
            Endpoint = new Uri(endpoint),
            Body = Encoding.UTF8.GetBytes(body),
            EventId = eventId,
        };
        var result = WebhookDeliveryResult.Failure(attempts, statusCode: 500, finalException: null);
        return new DeadLetterEntry(message, result, at ?? AbandonedAt);
    }
}
