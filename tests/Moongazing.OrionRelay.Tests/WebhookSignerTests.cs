namespace Moongazing.OrionRelay.Tests;

using System.Text;

using Moongazing.OrionRelay.Signing;

using Xunit;

public sealed class WebhookSignerTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Sign_produces_the_t_v1_envelope()
    {
        var signer = new WebhookSigner("topsecret");
        var value = signer.Sign(Encoding.UTF8.GetBytes("{\"hello\":\"world\"}"), At);

        Assert.StartsWith("t=1700000000,v1=", value, StringComparison.Ordinal);
        var hex = value.Split("v1=")[1];
        Assert.Equal(64, hex.Length); // SHA-256 -> 32 bytes -> 64 lowercase hex chars
        Assert.Equal(hex, hex.ToLowerInvariant());
    }

    [Fact]
    public void Sign_is_deterministic_for_the_same_inputs()
    {
        var signer = new WebhookSigner("topsecret");
        var body = Encoding.UTF8.GetBytes("payload");

        Assert.Equal(signer.Sign(body, At), signer.Sign(body, At));
    }

    [Fact]
    public void Sign_changes_with_the_timestamp()
    {
        var signer = new WebhookSigner("topsecret");
        var body = Encoding.UTF8.GetBytes("payload");

        var a = signer.Sign(body, At);
        var b = signer.Sign(body, At.AddSeconds(1));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sign_changes_with_the_secret()
    {
        var body = Encoding.UTF8.GetBytes("payload");

        Assert.NotEqual(
            new WebhookSigner("secret-a").Sign(body, At),
            new WebhookSigner("secret-b").Sign(body, At));
    }

    [Fact]
    public void Empty_secret_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WebhookSigner(string.Empty));
    }
}
