namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// The outcome of a delivery: whether it eventually succeeded, how many attempts it took, the
/// last HTTP status observed, and the final transport fault if one ended the attempts.
/// </summary>
public sealed class WebhookDeliveryResult
{
    private WebhookDeliveryResult(bool succeeded, int attempts, int? statusCode, Exception? finalException)
    {
        Succeeded = succeeded;
        Attempts = attempts;
        StatusCode = statusCode;
        FinalException = finalException;
    }

    /// <summary>True when a 2xx response was received within the attempt budget.</summary>
    public bool Succeeded { get; }

    /// <summary>The number of attempts made, including the first send.</summary>
    public int Attempts { get; }

    /// <summary>
    /// The HTTP status code of the last response received, or null when every attempt failed
    /// at the transport level before a response was produced.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The transport exception from the final attempt, when delivery ended on a transport fault
    /// rather than an HTTP error response. Null otherwise.
    /// </summary>
    public Exception? FinalException { get; }

    internal static WebhookDeliveryResult Success(int attempts, int statusCode) =>
        new(succeeded: true, attempts, statusCode, finalException: null);

    internal static WebhookDeliveryResult Failure(int attempts, int? statusCode, Exception? finalException) =>
        new(succeeded: false, attempts, statusCode, finalException);
}
