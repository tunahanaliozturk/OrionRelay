// NativeAOT publish smoke test for OrionRelay.
//
// Exercises the webhook signing/verification round trip end to end in a trimmed, AOT-published
// binary: sign a body, verify the signature is accepted, then confirm the two ways a receiver
// rejects a request — a tampered body and a stale timestamp. The signer/verifier is pure
// HMAC-SHA256 over spans with no reflection, so this locks in that the crypto path stays
// trim/AOT-clean for consumers who publish native.
//
// Exit 0 == every assertion held under NativeAOT. Any mismatch throws and fails the CI job.

using System.Text;
using Moongazing.OrionRelay.Signing;

const string secret = "whsec_orion_relay_aot_smoke";
byte[] body = Encoding.UTF8.GetBytes("""{"event":"order.paid","id":"evt_42"}""");

var signer = new WebhookSigner(secret);
var verifier = new WebhookVerifier(secret);

var sentAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
string signature = signer.Sign(body, sentAt);

// 1. A signature produced by our secret over the exact body verifies within the window.
var accepted = verifier.Verify(signature, body, sentAt);
Require(accepted.IsValid, $"a fresh signature should verify, got {accepted.Failure}");

// 2. Flip one body byte: the recomputed HMAC no longer matches, so it must be rejected.
byte[] tampered = (byte[])body.Clone();
tampered[0] ^= 0xFF;
var mismatch = verifier.Verify(signature, tampered, sentAt);
Require(!mismatch.IsValid, "a tampered body must fail verification");

// 3. Same signature, but the receiver's clock is well past the tolerance window: replay rejected.
var stale = verifier.Verify(signature, body, sentAt + WebhookVerifier.DefaultTolerance + TimeSpan.FromMinutes(1));
Require(!stale.IsValid, "a stale timestamp must fail verification");

Console.WriteLine("OrionRelay AOT smoke test passed.");
return 0;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"AOT smoke assertion failed: {message}");
    }
}
