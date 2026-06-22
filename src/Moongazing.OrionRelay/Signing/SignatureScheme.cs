namespace Moongazing.OrionRelay.Signing;

using System.Buffers.Text;
using System.Security.Cryptography;

/// <summary>
/// The single source of truth for the <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;</c> signature
/// contract: the wire-format tokens, the canonical preimage layout, and the HMAC primitive both
/// <see cref="WebhookSigner"/> and <see cref="WebhookVerifier"/> compute against. Centralising it
/// here is what keeps the sender and the receiver in lockstep: neither side carries its own copy of
/// the format, so they cannot drift apart.
/// </summary>
internal static class SignatureScheme
{
    /// <summary>The token introducing the timestamp segment, <c>t=</c>.</summary>
    internal const string TimestampPrefix = "t=";

    /// <summary>The separator between the timestamp segment and the signature segment.</summary>
    internal const char SegmentSeparator = ',';

    /// <summary>The token introducing the v1 signature segment, <c>v1=</c>.</summary>
    internal const string SignaturePrefix = "v1=";

    /// <summary>
    /// The byte joining the timestamp and the body in the preimage, so the MAC is taken over
    /// <c>&lt;unix-seconds&gt;.&lt;body&gt;</c> and the timestamp is bound into the signature.
    /// </summary>
    internal const byte PreimageSeparator = (byte)'.';

    /// <summary>The size of the HMAC-SHA256 output in bytes.</summary>
    internal const int MacSizeBytes = HMACSHA256.HashSizeInBytes;

    /// <summary>The length of the lowercase-hex rendering of the MAC.</summary>
    internal const int MacHexLength = MacSizeBytes * 2;

    /// <summary>
    /// The longest invariant decimal rendering of an <see cref="long"/> is
    /// <c>-9223372036854775808</c> (20 chars), so the timestamp digits never exceed this.
    /// </summary>
    internal const int MaxTimestampDigits = 20;

    /// <summary>The lowercase hex alphabet the signature is rendered with.</summary>
    internal const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// The maximum length of the rendered envelope: <see cref="TimestampPrefix"/> plus up to
    /// <see cref="MaxTimestampDigits"/> digits, the separator, <see cref="SignaturePrefix"/>, and
    /// <see cref="MacHexLength"/> hex chars.
    /// </summary>
    internal const int MaxEnvelopeChars =
        2 + MaxTimestampDigits + 1 + 3 + MacHexLength;

    /// <summary>
    /// Compute the HMAC-SHA256 of the canonical preimage <c>&lt;unix-seconds&gt;.&lt;body&gt;</c>
    /// into <paramref name="destination"/>. The unix-seconds value renders as ASCII digits
    /// (optionally a leading '-') and the separator is ASCII, so writing them as bytes is
    /// byte-identical to UTF-8 encoding the formatted preimage string. This is the exact
    /// computation both sides verify against, expressed once.
    /// </summary>
    /// <param name="secret">The HMAC key (the UTF-8 shared secret).</param>
    /// <param name="unixSeconds">The signed unix-seconds timestamp the signature binds.</param>
    /// <param name="body">The raw body bytes covered by the signature.</param>
    /// <param name="destination">A span of at least <see cref="MacSizeBytes"/> bytes for the MAC.</param>
    internal static void ComputeMac(
        ReadOnlySpan<byte> secret,
        long unixSeconds,
        ReadOnlySpan<byte> body,
        Span<byte> destination)
    {
        Span<byte> digits = stackalloc byte[MaxTimestampDigits];
        Utf8Formatter.TryFormat(unixSeconds, digits, out var digitsWritten);
        ComputeMac(secret, digits[..digitsWritten], body, destination);
    }

    /// <summary>
    /// Compute the MAC from already-formatted timestamp digits, avoiding a second
    /// <see cref="Utf8Formatter"/> pass on the signing hot path where the digits are rendered once
    /// and then reused both here and in the envelope.
    /// </summary>
    /// <param name="secret">The HMAC key (the UTF-8 shared secret).</param>
    /// <param name="timestampDigits">The ASCII unix-seconds digits, sign included, without the separator.</param>
    /// <param name="body">The raw body bytes covered by the signature.</param>
    /// <param name="destination">A span of at least <see cref="MacSizeBytes"/> bytes for the MAC.</param>
    internal static void ComputeMac(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> timestampDigits,
        ReadOnlySpan<byte> body,
        Span<byte> destination)
    {
        var prefixLength = timestampDigits.Length + 1;
        var signedLength = prefixLength + body.Length;
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(signedLength);
        try
        {
            var signed = rented.AsSpan(0, signedLength);
            timestampDigits.CopyTo(signed);
            signed[timestampDigits.Length] = PreimageSeparator;
            body.CopyTo(signed[prefixLength..]);

            HMACSHA256.HashData(secret, signed, destination);
        }
        finally
        {
            // The rented buffer held the request body; clear it before returning to the pool.
            System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>
    /// Render <paramref name="value"/> as lowercase hex into <paramref name="destination"/>, which
    /// must hold at least twice as many chars as <paramref name="value"/> has bytes. Returns the
    /// number of chars written.
    /// </summary>
    internal static int WriteHex(ReadOnlySpan<byte> value, Span<char> destination)
    {
        var cursor = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var b = value[i];
            destination[cursor++] = HexDigits[b >> 4];
            destination[cursor++] = HexDigits[b & 0x0F];
        }

        return cursor;
    }
}
