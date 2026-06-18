namespace Moongazing.OrionRelay.Demo;

using System.Text;

using Moongazing.OrionRelay.Signing;

/// <summary>
/// Shows the receiver side of the same HMAC contract: recompute the MAC over the exact raw body,
/// compare in constant time, and reject (a) a tampered payload, (b) a tampered signature, and
/// (c) a stale timestamp outside the freshness window (replay protection).
/// </summary>
internal static class VerificationDemo
{
    public static void Run()
    {
        DemoConsole.Banner("2. Receiver-side verification (constant-time) + tamper rejection");

        var signer = new WebhookSigner(SigningDemo.Secret);
        var body = Encoding.UTF8.GetBytes("""{"event":"order.created","id":"ord_1024","amount":4200}""");

        // The sender signs at 'now'; the receiver verifies against the same clock.
        var sentAt = DateTimeOffset.UtcNow;
        var header = signer.Sign(body, sentAt);
        var tolerance = TimeSpan.FromMinutes(5);

        DemoConsole.Item("Orion-Signature", header);
        DemoConsole.Item("Freshness window", $"+/- {tolerance.TotalMinutes:0} min");

        DemoConsole.Section("Genuine request, untouched body");
        Report(WebhookSignatureVerifier.Verify(header, body, SigningDemo.Secret, tolerance, sentAt));

        DemoConsole.Section("Tampered payload (one byte flipped after signing)");
        var tamperedBody = (byte[])body.Clone();
        tamperedBody[^2] ^= 0x01; // flip a bit in the original amount digit region
        DemoConsole.Note($"Received body: {Encoding.UTF8.GetString(tamperedBody)}");
        Report(WebhookSignatureVerifier.Verify(header, tamperedBody, SigningDemo.Secret, tolerance, sentAt));

        DemoConsole.Section("Tampered signature (attacker forged the hex MAC)");
        var forged = header[..^1] + (header[^1] == '0' ? '1' : '0');
        Report(WebhookSignatureVerifier.Verify(forged, body, SigningDemo.Secret, tolerance, sentAt));

        DemoConsole.Section("Replay: valid signature but stale timestamp");
        var verifyMuchLater = sentAt + tolerance + TimeSpan.FromMinutes(1);
        DemoConsole.Note($"Receiver clock is {(verifyMuchLater - sentAt).TotalMinutes:0} min past the send time.");
        Report(WebhookSignatureVerifier.Verify(header, body, SigningDemo.Secret, tolerance, verifyMuchLater));
    }

    private static void Report(WebhookSignatureVerifier.Outcome outcome)
    {
        switch (outcome)
        {
            case WebhookSignatureVerifier.Outcome.Valid:
                DemoConsole.Ok("Signature valid, timestamp fresh: accept and process.");
                break;
            case WebhookSignatureVerifier.Outcome.BadSignature:
                DemoConsole.Reject("Recomputed MAC did not match: refuse the request.");
                break;
            case WebhookSignatureVerifier.Outcome.Stale:
                DemoConsole.Reject("Timestamp outside freshness window: refuse as a possible replay.");
                break;
            case WebhookSignatureVerifier.Outcome.Malformed:
                DemoConsole.Reject("Signature header malformed: refuse the request.");
                break;
        }
    }
}
