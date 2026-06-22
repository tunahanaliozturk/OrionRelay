namespace Moongazing.OrionRelay.Signing;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC-SHA256 webhook signer. The signing secret is provided once at construction and never
/// leaves the instance. The signature binds the send timestamp into the MAC so a receiver that
/// enforces a freshness window can reject replayed requests. The wire format and the canonical
/// preimage live in <see cref="SignatureScheme"/>, which <see cref="WebhookVerifier"/> recomputes
/// against, so the signer and verifier cannot drift apart.
/// </summary>
public sealed class WebhookSigner : IWebhookSigner
{
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

        // Render the unix-seconds digits once. They are ASCII (optionally a leading '-'), so the
        // bytes are reused verbatim both for the MAC preimage and the envelope text below.
        Span<byte> digits = stackalloc byte[SignatureScheme.MaxTimestampDigits];
        Utf8Formatter.TryFormat(unixSeconds, digits, out var digitsWritten);
        var timestampDigits = digits[..digitsWritten];

        Span<byte> mac = stackalloc byte[SignatureScheme.MacSizeBytes];
        SignatureScheme.ComputeMac(secret, timestampDigits, body, mac);

        // Render "t=<unix-seconds>,v1=<lowercase-hex-mac>" once, using the shared scheme tokens so
        // the format cannot drift from what the verifier parses.
        Span<char> envelope = stackalloc char[SignatureScheme.MaxEnvelopeChars];
        var cursor = 0;

        SignatureScheme.TimestampPrefix.AsSpan().CopyTo(envelope);
        cursor += SignatureScheme.TimestampPrefix.Length;
        for (var i = 0; i < timestampDigits.Length; i++)
        {
            envelope[cursor++] = (char)timestampDigits[i];
        }

        envelope[cursor++] = SignatureScheme.SegmentSeparator;
        SignatureScheme.SignaturePrefix.AsSpan().CopyTo(envelope[cursor..]);
        cursor += SignatureScheme.SignaturePrefix.Length;

        cursor += SignatureScheme.WriteHex(mac, envelope[cursor..]);

        return new string(envelope[..cursor]);
    }
}
