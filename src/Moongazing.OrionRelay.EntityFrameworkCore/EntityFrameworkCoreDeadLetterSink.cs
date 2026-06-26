namespace Moongazing.OrionRelay.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.Delivery;

/// <summary>
/// A durable <see cref="IDeadLetterSink"/> backed by Entity Framework Core, so abandoned webhook
/// deliveries survive a process restart and are shared across instances pointed at one database.
/// The dispatcher routes each exhausted delivery here exactly once; this sink persists the delivery
/// (its endpoint, payload, headers, attempt count, terminal error, and abandonment timestamp) as a
/// <see cref="DeadLetterRecord"/> and exposes a read-back path so operators can inspect and triage
/// the held deliveries.
/// </summary>
/// <remarks>
/// <para>
/// The <c>IDeadLetterSink</c> interface is not widened: this is a durable reference implementation
/// behind the existing seam. <see cref="GetHeldAsync"/> and <see cref="CountAsync"/> are additive
/// query methods on this concrete store, not part of the interface contract, so an operator who
/// holds the sink as its concrete type can inspect what is parked.
/// </para>
/// <para>
/// The write is idempotent on the delivery id. <see cref="DeadLetterRecord.DeliveryId"/> is the
/// primary key, so when the dispatcher re-routes a replayed terminal delivery the second write
/// resolves to the existing row by key rather than inserting a duplicate. A concurrent first insert
/// for the same id is rejected by the database with a unique-constraint violation that surfaces as a
/// <see cref="DbUpdateException"/>; the loser confirms the duplicate by re-reading the row rather
/// than inspecting a provider-specific SQL error code, so the sink stays provider-agnostic and a
/// genuinely different failure (for example a missing table) is rethrown instead of being mistaken
/// for a duplicate.
/// </para>
/// <para>
/// Every operation uses a fresh <see cref="DbContext"/> from the injected
/// <see cref="IDbContextFactory{TContext}"/>, because a context is not safe for concurrent use and a
/// short-lived context per operation keeps no state in memory between abandoned deliveries.
/// </para>
/// </remarks>
/// <typeparam name="TContext">
/// The context type that maps <see cref="DeadLetterRecord"/>. Use
/// <see cref="OrionRelayDeadLetterDbContext"/> for a dedicated context, or any context that applies
/// <see cref="DeadLetterRecordConfiguration"/>.
/// </typeparam>
public sealed class EntityFrameworkCoreDeadLetterSink<TContext> : IDeadLetterSink
    where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> contextFactory;

    /// <summary>Create a sink that draws a fresh context per operation from the factory.</summary>
    /// <param name="contextFactory">Supplies a fresh context per operation.</param>
    public EntityFrameworkCoreDeadLetterSink(IDbContextFactory<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        this.contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var record = ToRecord(entry);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Re-routing a replayed terminal delivery must land once. Update the existing row in place
        // when the id is already parked; otherwise insert. Reading first keeps this idempotent for
        // the common sequential re-route, and the catch below covers the concurrent-write race.
        var existing = await context.Set<DeadLetterRecord>()
            .FirstOrDefaultAsync(e => e.DeliveryId == record.DeliveryId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.Add(record);
        }
        else
        {
            CopyMutableFields(record, existing);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent caller inserted this id first, so our insert hit the primary key's unique
            // constraint. Confirm that by re-reading on a clean context rather than sniffing a
            // provider error code (this package references no provider): if the row is now present the
            // delivery is already parked under this id and the re-route is satisfied; if it is absent
            // the failure was something else and must surface.
            await ReconcileConflictAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Read back the held deliveries, newest abandonment first, for inspection and triage. This is an
    /// additive query on the durable store, not part of <see cref="IDeadLetterSink"/>.
    /// </summary>
    /// <param name="limit">
    /// The maximum number of records to return. When null, every held record is returned; supply a
    /// cap when the parked set may be large.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The held records, ordered newest abandonment first.</returns>
    public async Task<IReadOnlyList<DeadLetterRecord>> GetHeldAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit cannot be negative.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Newest-first is the order an operator triaging recent failures wants; it is served by the
        // abandonment-timestamp index. AsNoTracking because these rows are read for inspection, never
        // mutated through the returned instances.
        IQueryable<DeadLetterRecord> query = context.Set<DeadLetterRecord>()
            .AsNoTracking()
            .OrderByDescending(e => e.DeadLetteredAtTicks);

        if (limit is int take)
        {
            query = query.Take(take);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Count the held deliveries. This is an additive query on the durable store, not part of
    /// <see cref="IDeadLetterSink"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of records currently parked.</returns>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Set<DeadLetterRecord>()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReconcileConflictAsync(DeadLetterRecord record, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var winner = await context.Set<DeadLetterRecord>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.DeliveryId == record.DeliveryId, cancellationToken)
            .ConfigureAwait(false);

        if (winner is not null)
        {
            // The id is already parked: the concurrent writer won and the re-route is satisfied. The
            // sink records the abandonment once; the winner's copy stands.
            return;
        }

        // No row exists, so the SaveChanges failure was not a duplicate-key collision. Surface the
        // genuine error by re-running the insert on a clean context.
        await using var retryContext = await contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        retryContext.Add(record);
        await retryContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DeadLetterRecord ToRecord(DeadLetterEntry entry)
    {
        var message = entry.Message;
        var result = entry.Result;

        // EventId is the stable identity a receiver deduplicates on, so it is the natural key for an
        // idempotent re-route. When the message carried none there is nothing stable to dedupe on and
        // each abandonment is a distinct event, so a per-write surrogate keys the row.
        var deliveryId = string.IsNullOrEmpty(message.EventId)
            ? Guid.NewGuid().ToString("N")
            : message.EventId;

        return new DeadLetterRecord
        {
            DeliveryId = deliveryId,
            Endpoint = message.Endpoint.AbsoluteUri,
            Body = message.Body.ToArray(),
            ContentType = message.ContentType,
            EventType = message.EventType,
            EventId = message.EventId,
            Attempts = result.Attempts,
            StatusCode = result.StatusCode,
            FinalError = result.FinalException?.Message,
            DeadLetteredAtTicks = entry.DeadLetteredAt.UtcTicks,
        };
    }

    private static void CopyMutableFields(DeadLetterRecord from, DeadLetterRecord into)
    {
        // Refresh the parked copy from the latest terminal result so a re-route reflects the most
        // recent abandonment, without touching the key.
        into.Endpoint = from.Endpoint;
        into.Body = from.Body;
        into.ContentType = from.ContentType;
        into.EventType = from.EventType;
        into.EventId = from.EventId;
        into.Attempts = from.Attempts;
        into.StatusCode = from.StatusCode;
        into.FinalError = from.FinalError;
        into.DeadLetteredAtTicks = from.DeadLetteredAtTicks;
    }
}
