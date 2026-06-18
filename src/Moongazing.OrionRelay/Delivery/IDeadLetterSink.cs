namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// Destination for deliveries that exhausted their retry budget. The dispatcher routes each
/// abandoned message here exactly once, carrying its terminal failure context, so a consumer can
/// persist, alert on, or later replay it. Register an implementation via DI to override the
/// in-memory default.
/// </summary>
public interface IDeadLetterSink
{
    /// <summary>
    /// Accept an exhausted delivery. Called once, after the final failed attempt. Implementations
    /// should be resilient: the dispatcher swallows any fault raised here so a sink outage cannot
    /// turn an already-failed delivery into an exception for the caller.
    /// </summary>
    /// <param name="entry">The abandoned message and its terminal failure context.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default);
}
