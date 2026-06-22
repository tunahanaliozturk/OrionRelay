namespace Moongazing.OrionRelay.Signing;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC-SHA256 webhook verifier. The shared secret and the freshness window are provided once at
/// construction. Verification recomputes the MAC over the same canonical preimage
/// (<c>&lt;unix-seconds&gt;.&lt;body&gt;</c>) the signer uses, by way of <see cref="SignatureScheme"/>, so
/// the two sides cannot drift; it rejects timestamps outside the window to stop replays, and it
/// compares signatures in constant time so a near-miss MAC cannot be discovered through response
/// timing.
/// </summary>
public sealed class WebhookVerifier : IWebhookVerifier
{
    /// <summary>The freshness window applied when a caller does not specify one.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    private readonly byte[] secret;
    private readonly TimeSpan tolerance;

    /// <summary>Create a verifier over a UTF-8 shared secret using <see cref="DefaultTolerance"/>.</summary>
    /// <param name="secret">The shared secret. Must be non-empty and match the signer's secret.</param>
    public WebhookVerifier(string secret)
        : this(secret, DefaultTolerance)
    {
    }

    /// <summary>Create a verifier over a UTF-8 shared secret with an explicit freshness window.</summary>
    /// <param name="secret">The shared secret. Must be non-empty and match the signer's secret.</param>
    /// <param name="tolerance">
    /// The maximum absolute difference allowed between the signed timestamp and the verification
    /// time, in either direction. Must be non-negative. A request whose timestamp is older or more
    /// future-skewed than this is rejected as a possible replay.
    /// </param>
    public WebhookVerifier(string secret, TimeSpan tolerance)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (tolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance), tolerance, "Tolerance cannot be negative.");
        }

        this.secret = Encoding.UTF8.GetBytes(secret);
        this.tolerance = tolerance;
    }

    /// <inheritdoc />
    public WebhookVerificationResult Verify(
        string signatureHeader, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (!TryParse(signatureHeader, out var unixSeconds, out var sentMac))
        {
            return WebhookVerificationResult.Invalid(WebhookVerificationFailure.Malformed);
        }

        // Enforce the freshness window before computing the MAC: a replayed or clock-skewed request
        // is rejected without spending HMAC work, and the timestamp is the cheaper check.
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var skew = now - sentAt;
        if (skew > tolerance || skew < -tolerance)
        {
            return WebhookVerificationResult.Invalid(WebhookVerificationFailure.StaleTimestamp);
        }

        Span<byte> expected = stackalloc byte[SignatureScheme.MacSizeBytes];
        SignatureScheme.ComputeMac(secret, unixSeconds, body, expected);

        // Constant-time compare over the raw MAC bytes so a partial match cannot be inferred from
        // how long the comparison takes. Both spans are exactly MacSizeBytes by construction.
        var match = CryptographicOperations.FixedTimeEquals(expected, sentMac);

        return match
            ? WebhookVerificationResult.Valid
            : WebhookVerificationResult.Invalid(WebhookVerificationFailure.SignatureMismatch);
    }

    /// <summary>
    /// Parse "t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;" strictly into its timestamp and decoded MAC bytes.
    /// Anything that does not match the exact shape, including a wrong-length or non-hex MAC, fails.
    /// </summary>
    private static bool TryParse(string header, out long unixSeconds, out byte[] sentMac)
    {
        unixSeconds = 0;
        sentMac = [];

        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var separatorIndex = header.IndexOf(SignatureScheme.SegmentSeparator);
        if (separatorIndex < 0)
        {
            return false;
        }

        var timestampSegment = header.AsSpan(0, separatorIndex);
        var signatureSegment = header.AsSpan(separatorIndex + 1);

        if (!timestampSegment.StartsWith(SignatureScheme.TimestampPrefix)
            || !signatureSegment.StartsWith(SignatureScheme.SignaturePrefix))
        {
            return false;
        }

        var timestampText = timestampSegment[SignatureScheme.TimestampPrefix.Length..];
        var macHex = signatureSegment[SignatureScheme.SignaturePrefix.Length..];

        // A second separator (a third segment) means the header is not the exact two-segment shape.
        if (macHex.Contains(SignatureScheme.SegmentSeparator))
        {
            return false;
        }

        if (macHex.Length != SignatureScheme.MacHexLength
            || !long.TryParse(
                timestampText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out unixSeconds))
        {
            return false;
        }

        var decoded = new byte[SignatureScheme.MacSizeBytes];
        if (!TryDecodeLowerHex(macHex, decoded))
        {
            return false;
        }

        sentMac = decoded;
        return true;
    }

    /// <summary>
    /// Decode a lowercase-hex span into <paramref name="destination"/>. Rejects any non-hex or
    /// uppercase character so the verifier accepts exactly the lowercase rendering the signer emits.
    /// </summary>
    private static bool TryDecodeLowerHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            if (!TryHexNibble(hex[i * 2], out var high)
                || !TryHexNibble(hex[(i * 2) + 1], out var low))
            {
                return false;
            }

            destination[i] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static bool TryHexNibble(char c, out int value)
    {
        if (c >= '0' && c <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c >= 'a' && c <= 'f')
        {
            value = 10 + (c - 'a');
            return true;
        }

        value = 0;
        return false;
    }
}
