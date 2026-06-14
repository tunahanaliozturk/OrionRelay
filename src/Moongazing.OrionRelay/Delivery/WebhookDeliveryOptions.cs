namespace Moongazing.OrionRelay.Delivery;

/// <summary>
/// Tuning for outbound webhook delivery: how many times to try, how long to back off between
/// tries, the per-request timeout, and the signature header to emit.
/// </summary>
public sealed class WebhookDeliveryOptions
{
    /// <summary>
    /// Total delivery attempts, including the first. Must be at least 1. Default 4
    /// (one initial send plus three retries).
    /// </summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>The base backoff delay, doubled each retry. Default 1 second.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The cap the exponential backoff is clamped to. Default 30 seconds.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The per-attempt HTTP timeout. Default 30 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The header carrying the signature value (see <see cref="Signing.IWebhookSigner"/>).
    /// Default <c>Orion-Signature</c>.
    /// </summary>
    public string SignatureHeader { get; set; } = "Orion-Signature";

    internal void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts,
                "MaxAttempts must be at least 1.");
        }
        if (BaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), BaseDelay,
                "BaseDelay cannot be negative.");
        }
        if (MaxDelay < BaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), MaxDelay,
                "MaxDelay cannot be less than BaseDelay.");
        }
    }
}
