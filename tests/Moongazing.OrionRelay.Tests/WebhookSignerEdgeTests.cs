namespace Moongazing.OrionRelay.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Moongazing.OrionRelay.Signing;

using Xunit;

/// <summary>
/// Edge and cross-verification coverage for <see cref="WebhookSigner"/> that complements the
/// happy-path envelope tests in <see cref="WebhookSignerTests"/>.
/// </summary>
public sealed class WebhookSignerEdgeTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    /// <summary>
    /// Independently recompute the HMAC the way the documented scheme says (HMAC-SHA256 over
    /// "&lt;unix-seconds&gt;.&lt;body&gt;", lowercase hex) and assert the signer matches it byte for byte.
    /// This pins the wire contract, not just internal self-consistency.
    /// </summary>
    [Fact]
    public void Sign_matches_an_independently_computed_hmac()
    {
        const string secret = "topsecret";
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

        var signed = signer().Sign(body, At);

        var unix = At.ToUnixTimeSeconds();
        var preimage = BuildPreimage(unix, body);
        var expectedMac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), preimage);
        var expectedHex = Convert.ToHexString(expectedMac).ToLowerInvariant();
        var expected = string.Create(CultureInfo.InvariantCulture, $"t={unix},v1={expectedHex}");

        Assert.Equal(expected, signed);

        static WebhookSigner signer() => new(secret);
    }

    [Fact]
    public void Sign_changes_with_the_body()
    {
        var s = new WebhookSigner("topsecret");

        Assert.NotEqual(
            s.Sign(Encoding.UTF8.GetBytes("payload-a"), At),
            s.Sign(Encoding.UTF8.GetBytes("payload-b"), At));
    }

    [Fact]
    public void Sign_handles_an_empty_body()
    {
        var s = new WebhookSigner("topsecret");

        var value = s.Sign(ReadOnlySpan<byte>.Empty, At);

        // The preimage is just "<unix>." with no body, still a valid 64-hex MAC.
        Assert.StartsWith("t=1700000000,v1=", value, StringComparison.Ordinal);
        Assert.Equal(64, value.Split("v1=")[1].Length);

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("topsecret"),
            Encoding.UTF8.GetBytes("1700000000."));
        Assert.Equal(
            Convert.ToHexString(expected).ToLowerInvariant(),
            value.Split("v1=")[1]);
    }

    [Fact]
    public void Sign_emits_the_raw_unix_seconds_including_negative_pre_epoch_values()
    {
        var s = new WebhookSigner("topsecret");
        var preEpoch = DateTimeOffset.FromUnixTimeSeconds(-5);

        var value = s.Sign(Encoding.UTF8.GetBytes("x"), preEpoch);

        // Documents that the signer stamps the raw signed integer, sign included.
        Assert.StartsWith("t=-5,v1=", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_uses_whole_seconds_and_ignores_sub_second_precision()
    {
        var s = new WebhookSigner("topsecret");
        var body = Encoding.UTF8.GetBytes("payload");

        var whole = s.Sign(body, At);
        var withMillis = s.Sign(body, At.AddMilliseconds(750));

        // ToUnixTimeSeconds truncates, so both round to the same second and the same signature.
        Assert.Equal(whole, withMillis);
    }

    [Fact]
    public void Null_secret_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new WebhookSigner(null!));
    }

    private static byte[] BuildPreimage(long unix, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{unix}."));
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.CopyTo(buffer, prefix.Length);
        return buffer;
    }
}
