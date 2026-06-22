namespace Moongazing.OrionRelay.Signing;

/// <summary>
/// Why a webhook signature failed verification. <see cref="None"/> is paired with a valid result;
/// every other value names a distinct, non-overlapping rejection reason so a receiver can log,
/// alert, or respond differently per cause.
/// </summary>
public enum WebhookVerificationFailure
{
    /// <summary>The signature verified: the MAC matched and the timestamp was within the window.</summary>
    None = 0,

    /// <summary>
    /// The signature header could not be parsed into the <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;</c>
    /// shape: it was null, empty, missing a segment, carried an unexpected token, or held a
    /// non-numeric timestamp or a malformed hex MAC. The MAC is never computed in this case.
    /// </summary>
    Malformed = 1,

    /// <summary>
    /// The header parsed, but its timestamp fell outside the freshness window in either direction
    /// (too old, or skewed into the future beyond tolerance). The request is rejected as a possible
    /// replay before the MAC is compared.
    /// </summary>
    StaleTimestamp = 2,

    /// <summary>
    /// The header parsed and the timestamp was fresh, but the recomputed MAC did not match the one
    /// in the header. The body, the timestamp, or the signature was altered, or the secret differs.
    /// </summary>
    SignatureMismatch = 3,
}
