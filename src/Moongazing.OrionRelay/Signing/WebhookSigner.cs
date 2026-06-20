namespace Moongazing.OrionRelay.Signing;

using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC-SHA256 webhook signer. The signing secret is provided once at construction and never
/// leaves the instance. The signature binds the send timestamp into the MAC so a receiver that
/// enforces a freshness window can reject replayed requests.
/// </summary>
public sealed class WebhookSigner : IWebhookSigner
{
    // The longest invariant decimal rendering of an Int64 is "-9223372036854775808" (20 chars),
    // so 24 bytes comfortably holds "<unix-seconds>." for every representable timestamp.
    private const int MaxPrefixBytes = 24;

    // Envelope is "t=" + <=20 digit unix seconds + ",v1=" + 64 hex chars.
    private const int MaxEnvelopeChars = 2 + 20 + 4 + (HMACSHA256.HashSizeInBytes * 2);

    private const string HexDigits = "0123456789abcdef";

    private readonly byte[] secret;

    /// <summary>Create a signer over a UTF-8 shared secret.</summary>
    /// <param name="secret">The shared secret. Must be non-empty.</param>
    public WebhookSigner(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        this.secret = Encoding.UTF8.GetBytes(secret);
    }

    /// <inheritdoc />
    public string Sign(ReadOnlySpan<byte> body, DateTimeOffset timestamp)
    {
        var unixSeconds = timestamp.ToUnixTimeSeconds();

        // Build the preimage "<unix-seconds>.<body>" directly into a pooled buffer. The unix-seconds
        // value renders as ASCII digits (optionally a leading '-') and '.' is ASCII, so writing them
        // as bytes is byte-identical to UTF-8 encoding the formatted string.
        Span<byte> prefix = stackalloc byte[MaxPrefixBytes];
        Utf8Formatter.TryFormat(unixSeconds, prefix, out var digitsWritten);
        prefix[digitsWritten] = (byte)'.';
        var prefixLength = digitsWritten + 1;

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var signedLength = prefixLength + body.Length;
        var rented = ArrayPool<byte>.Shared.Rent(signedLength);
        try
        {
            var signed = rented.AsSpan(0, signedLength);
            prefix[..prefixLength].CopyTo(signed);
            body.CopyTo(signed[prefixLength..]);

            HMACSHA256.HashData(secret, signed, mac);
        }
        finally
        {
            // The rented buffer held the request body; clear it before returning to the pool.
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        // Render "t=<unix-seconds>,v1=<lowercase-hex-mac>" once, writing the hex straight in
        // lowercase rather than uppercasing then lowercasing an intermediate string. The unix-seconds
        // digits were already formatted above, so reuse them verbatim.
        Span<char> envelope = stackalloc char[MaxEnvelopeChars];
        var cursor = 0;
        envelope[cursor++] = 't';
        envelope[cursor++] = '=';
        for (var i = 0; i < digitsWritten; i++)
        {
            envelope[cursor++] = (char)prefix[i];
        }

        envelope[cursor++] = ',';
        envelope[cursor++] = 'v';
        envelope[cursor++] = '1';
        envelope[cursor++] = '=';

        for (var i = 0; i < mac.Length; i++)
        {
            var b = mac[i];
            envelope[cursor++] = HexDigits[b >> 4];
            envelope[cursor++] = HexDigits[b & 0x0F];
        }

        return new string(envelope[..cursor]);
    }
}
