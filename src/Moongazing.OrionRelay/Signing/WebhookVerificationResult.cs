namespace Moongazing.OrionRelay.Signing;

/// <summary>
/// The outcome of verifying a webhook signature: whether it is valid and, when not, the single
/// reason it was rejected. Returned as a value rather than thrown so a receiver can branch on the
/// reason without exception control flow on a hot request path.
/// </summary>
public readonly struct WebhookVerificationResult : IEquatable<WebhookVerificationResult>
{
    private WebhookVerificationResult(bool isValid, WebhookVerificationFailure failure)
    {
        IsValid = isValid;
        Failure = failure;
    }

    /// <summary>
    /// True when the signature is authentic and fresh. When false, <see cref="Failure"/> names the
    /// reason.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// The reason verification failed, or <see cref="WebhookVerificationFailure.None"/> when
    /// <see cref="IsValid"/> is true.
    /// </summary>
    public WebhookVerificationFailure Failure { get; }

    /// <summary>A successful verification.</summary>
    internal static WebhookVerificationResult Valid { get; } =
        new(isValid: true, WebhookVerificationFailure.None);

    /// <summary>A failed verification carrying the reason it was rejected.</summary>
    /// <param name="failure">The non-<see cref="WebhookVerificationFailure.None"/> reason.</param>
    internal static WebhookVerificationResult Invalid(WebhookVerificationFailure failure) =>
        new(isValid: false, failure);

    /// <inheritdoc />
    public bool Equals(WebhookVerificationResult other) =>
        IsValid == other.IsValid && Failure == other.Failure;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is WebhookVerificationResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsValid, Failure);

    /// <summary>Equality over <see cref="IsValid"/> and <see cref="Failure"/>.</summary>
    public static bool operator ==(WebhookVerificationResult left, WebhookVerificationResult right) =>
        left.Equals(right);

    /// <summary>Inequality over <see cref="IsValid"/> and <see cref="Failure"/>.</summary>
    public static bool operator !=(WebhookVerificationResult left, WebhookVerificationResult right) =>
        !left.Equals(right);
}
