namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// An in-memory <see cref="IDeadLetterSink"/>: holds the most recent abandoned deliveries in
/// arrival order, up to a fixed <see cref="Capacity"/>. When full, writing a new entry evicts the
/// oldest one, so a prolonged receiver outage cannot grow the process working set without bound.
/// Intended for tests, demos, and single-process apps; entries are lost on restart, so register a
/// durable sink for production. Not registered by default (the default sink is a no-op); opt in
/// explicitly when you want to inspect recent failures in-process. Thread-safe.
/// </summary>
public sealed class InMemoryDeadLetterSink : IDeadLetterSink
{
    /// <summary>The default retained-entry cap when no capacity is supplied.</summary>
    public const int DefaultCapacity = 1024;

    private readonly object gate = new();
    private readonly Queue<DeadLetterEntry> entries;

    /// <summary>Create a sink that retains up to <see cref="DefaultCapacity"/> entries.</summary>
    public InMemoryDeadLetterSink()
        : this(DefaultCapacity)
    {
    }

    /// <summary>Create a sink that retains up to <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">
    /// The maximum number of entries to retain. Once reached, each new write evicts the oldest
    /// entry. Must be positive.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> is less than 1.
    /// </exception>
    public InMemoryDeadLetterSink(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        entries = new Queue<DeadLetterEntry>(Math.Min(capacity, 16));
    }

    /// <summary>The maximum number of entries retained before the oldest is evicted.</summary>
    public int Capacity { get; }

    /// <summary>The number of deliveries currently retained.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>A point-in-time snapshot of the retained entries, oldest first.</summary>
    public IReadOnlyList<DeadLetterEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            // Bounded retention: evict oldest-first so capture never outgrows Capacity.
            while (entries.Count >= Capacity)
            {
                entries.Dequeue();
            }

            entries.Enqueue(entry);
        }

        return Task.CompletedTask;
    }
}
