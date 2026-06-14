namespace Moongazing.OrionRelay.Signing;

/// <summary>
/// Produces the signature header value a receiver uses to verify that a webhook request
/// was sent by this service and has not been tampered with or replayed.
/// </summary>
public interface IWebhookSigner
{
    /// <summary>
    /// Compute the signature header value for a request body sent at a given timestamp.
    /// The returned value is of the form <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;</c>, where the
    /// HMAC is taken over <c>&lt;unix-seconds&gt;.&lt;body&gt;</c> so the timestamp is bound into the
    /// signature and a captured request cannot be replayed under a different time.
    /// </summary>
    /// <param name="body">The exact request body bytes that will be transmitted.</param>
    /// <param name="timestamp">The send timestamp stamped into the signature.</param>
    /// <returns>The signature header value.</returns>
    string Sign(ReadOnlySpan<byte> body, DateTimeOffset timestamp);
}
