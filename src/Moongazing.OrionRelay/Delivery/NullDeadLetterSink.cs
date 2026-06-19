namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// A no-op <see cref="IDeadLetterSink"/> that discards every exhausted delivery. This is the
/// default sink wired by <c>AddOrionRelay</c>: it retains nothing, so it cannot grow the process
/// working set during a prolonged receiver outage. Opt in to <see cref="InMemoryDeadLetterSink"/>
/// or register a durable sink when you need to inspect, alert on, or replay abandoned deliveries.
/// </summary>
public sealed class NullDeadLetterSink : IDeadLetterSink
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NullDeadLetterSink Instance = new();

    private NullDeadLetterSink()
    {
    }

    /// <inheritdoc />
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
