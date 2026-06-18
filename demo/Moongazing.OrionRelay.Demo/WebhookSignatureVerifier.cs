namespace Moongazing.OrionRelay.Demo;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Receiver-side counterpart to <c>Moongazing.OrionRelay.Signing.WebhookSigner</c>. The library
/// ships only the sender side; this mirrors the same HMAC contract a real receiver would implement.
/// It recomputes the MAC over the exact raw body and compares in constant time, and rejects
/// requests whose timestamp falls outside a freshness window so a captured request cannot be replayed.
/// </summary>
internal static class WebhookSignatureVerifier
{
    public enum Outcome
    {
        Valid,
        Malformed,
        Stale,
        BadSignature,
    }

    /// <summary>
    /// Verify an <c>Orion-Signature</c> header of the form <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;</c>
    /// against the raw body bytes exactly as received.
    /// </summary>
    public static Outcome Verify(
        string signatureHeader,
        ReadOnlySpan<byte> rawBody,
        string secret,
        TimeSpan tolerance,
        DateTimeOffset now)
    {
        if (!TryParse(signatureHeader, out var unixSeconds, out var sentMac))
        {
            return Outcome.Malformed;
        }

        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var skew = now - sentAt;
        if (skew > tolerance || skew < -tolerance)
        {
            return Outcome.Stale; // outside the freshness window: reject as a possible replay
        }

        var expected = ComputeExpected(unixSeconds, rawBody, secret);

        // Constant-time compare to avoid leaking the MAC through timing.
        var match = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(sentMac));

        return match ? Outcome.Valid : Outcome.BadSignature;
    }

    private static bool TryParse(string header, out long unixSeconds, out string sentMac)
    {
        unixSeconds = 0;
        sentMac = string.Empty;

        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var parts = header.Split(',');
        if (parts.Length != 2
            || !parts[0].StartsWith("t=", StringComparison.Ordinal)
            || !parts[1].StartsWith("v1=", StringComparison.Ordinal))
        {
            return false;
        }

        var timestampText = parts[0]["t=".Length..];
        sentMac = parts[1]["v1=".Length..];

        return long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds)
            && sentMac.Length > 0;
    }

    private static string ComputeExpected(long unixSeconds, ReadOnlySpan<byte> rawBody, string secret)
    {
        var prefix = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{unixSeconds}."));

        var signed = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(signed.AsSpan());
        rawBody.CopyTo(signed.AsSpan(prefix.Length));

        return Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)).ToLowerInvariant();
    }
}
