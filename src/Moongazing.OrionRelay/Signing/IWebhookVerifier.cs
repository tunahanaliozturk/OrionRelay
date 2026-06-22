namespace Moongazing.OrionRelay.Signing;

/// <summary>
/// Verifies that an incoming webhook carries a signature this service's secret produced, over the
/// exact body received, within a freshness window. The receiver-side counterpart to
/// <see cref="IWebhookSigner"/>: it recomputes the same HMAC over the same canonical preimage and
/// rejects anything that does not match or has fallen outside the window.
/// </summary>
public interface IWebhookVerifier
{
    /// <summary>
    /// Verify a signature header of the form <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;</c> against the
    /// raw body bytes exactly as received, relative to the supplied current time.
    /// </summary>
    /// <param name="signatureHeader">
    /// The signature header value (for example the <c>Orion-Signature</c> header).
    /// </param>
    /// <param name="body">
    /// The raw request body bytes exactly as received, before any deserialization reshapes them.
    /// </param>
    /// <param name="now">
    /// The current time the timestamp's freshness is measured against. Pass the receiver's clock;
    /// an explicit value keeps verification deterministic under test.
    /// </param>
    /// <returns>
    /// A <see cref="WebhookVerificationResult"/> that is valid, or carries the single reason it was
    /// rejected (malformed header, stale timestamp, or signature mismatch).
    /// </returns>
    WebhookVerificationResult Verify(string signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now);
}
