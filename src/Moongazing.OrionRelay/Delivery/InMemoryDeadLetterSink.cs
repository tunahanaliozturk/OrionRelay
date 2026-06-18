namespace Moongazing.OrionRelay.Delivery;

using System.Collections.Concurrent;

/// <summary>
/// The default <see cref="IDeadLetterSink"/>: holds abandoned deliveries in memory in arrival
/// order. Intended for tests, demos, and single-process apps; entries are lost on restart, so
/// register a durable sink for production. Thread-safe.
/// </summary>
public sealed class InMemoryDeadLetterSink : IDeadLetterSink
{
    private readonly ConcurrentQueue<DeadLetterEntry> entries = new();

    /// <summary>The number of deliveries captured so far.</summary>
    public int Count => entries.Count;

    /// <summary>A point-in-time snapshot of the captured entries, oldest first.</summary>
    public IReadOnlyList<DeadLetterEntry> Entries => entries.ToArray();

    /// <inheritdoc />
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}
