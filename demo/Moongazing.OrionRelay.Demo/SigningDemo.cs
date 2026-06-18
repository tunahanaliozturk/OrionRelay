namespace Moongazing.OrionRelay.Demo;

using System.Text;

using Moongazing.OrionRelay.Signing;

/// <summary>
/// Shows the sender side: <see cref="WebhookSigner"/> producing the
/// <c>t=&lt;unix-seconds&gt;,v1=&lt;hex-hmac&gt;</c> header value, and that the signer is deterministic
/// for a fixed (secret, body, timestamp) yet changes when any of those inputs change.
/// </summary>
internal static class SigningDemo
{
    public const string Secret = "whsec_demo_shared_secret";

    public static void Run()
    {
        DemoConsole.Banner("1. HMAC-SHA256 request signing (WebhookSigner.Sign)");

        var signer = new WebhookSigner(Secret);
        var body = Encoding.UTF8.GetBytes("""{"event":"order.created","id":"ord_1024","amount":4200}""");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var header = signer.Sign(body, timestamp);

        DemoConsole.Section("Signing a payload");
        DemoConsole.Item("Body", Encoding.UTF8.GetString(body));
        DemoConsole.Item("Timestamp (unix)", timestamp.ToUnixTimeSeconds().ToString());
        DemoConsole.Item("Orion-Signature", header);
        DemoConsole.Note("The MAC is taken over '<unix-seconds>.<body>', binding the timestamp into the signature.");

        DemoConsole.Section("Deterministic for the same inputs");
        var again = signer.Sign(body, timestamp);
        if (string.Equals(header, again, StringComparison.Ordinal))
        {
            DemoConsole.Ok("Re-signing identical (secret, body, timestamp) yields the identical header.");
        }
        else
        {
            DemoConsole.Note("[UNEXPECTED] re-signing produced a different header.");
        }

        DemoConsole.Section("Sensitive to every input");
        var laterTimestamp = signer.Sign(body, timestamp.AddSeconds(1));
        var otherBody = signer.Sign(Encoding.UTF8.GetBytes("tampered"), timestamp);
        var otherSecret = new WebhookSigner("whsec_a_different_secret").Sign(body, timestamp);

        DemoConsole.Item("+1s timestamp differs", (!string.Equals(header, laterTimestamp, StringComparison.Ordinal)).ToString());
        DemoConsole.Item("Different body differs", (!string.Equals(header, otherBody, StringComparison.Ordinal)).ToString());
        DemoConsole.Item("Different secret differs", (!string.Equals(header, otherSecret, StringComparison.Ordinal)).ToString());
    }
}
