namespace Moongazing.OrionRelay.Signing;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// HMAC-SHA256 webhook signer. The signing secret is provided once at construction and never
/// leaves the instance. The signature binds the send timestamp into the MAC so a receiver that
/// enforces a freshness window can reject replayed requests.
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
        var prefix = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{unixSeconds}."));

        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed.AsSpan());
        body.CopyTo(signed.AsSpan(prefix.Length));

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, signed, mac);

        var hex = Convert.ToHexString(mac).ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"t={unixSeconds},v1={hex}");
    }
}
