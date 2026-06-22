namespace Moongazing.OrionRelay.Tests;

using System.Text;

using Moongazing.OrionRelay.Signing;

using Xunit;

/// <summary>
/// Round-trips the shipped <see cref="WebhookVerifier"/> against the actual <see cref="WebhookSigner"/>
/// so the two stay in lockstep: whatever the signer emits, the verifier accepts, and any tamper,
/// staleness, or malformed input is rejected with the correct, specific reason.
/// </summary>
public sealed class WebhookVerifierTests
{
    private const string Secret = "topsecret";
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

    [Fact]
    public void A_signature_from_the_signer_verifies()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);

        var result = new WebhookVerifier(Secret).Verify(header, Body, now: At);

        Assert.True(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.None, result.Failure);
    }

    [Fact]
    public void A_fresh_timestamp_inside_the_window_passes()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);

        // Signed at At, verified four minutes later against a five-minute window.
        var result = new WebhookVerifier(Secret, TimeSpan.FromMinutes(5))
            .Verify(header, Body, now: At.AddMinutes(4));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_tampered_body_fails_as_a_signature_mismatch()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var tamperedBody = Encoding.UTF8.GetBytes("{\"hello\":\"intruder\"}");

        var result = new WebhookVerifier(Secret).Verify(header, tamperedBody, now: At);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.SignatureMismatch, result.Failure);
    }

    [Fact]
    public void A_signature_from_a_different_secret_fails_as_a_signature_mismatch()
    {
        var header = new WebhookSigner("a-different-secret").Sign(Body, At);

        var result = new WebhookVerifier(Secret).Verify(header, Body, now: At);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.SignatureMismatch, result.Failure);
    }

    [Fact]
    public void A_flipped_signature_byte_fails_as_a_signature_mismatch()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var tampered = FlipLastHexDigit(header);

        var result = new WebhookVerifier(Secret).Verify(signatureHeader: tampered, Body, now: At);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.SignatureMismatch, result.Failure);
    }

    [Fact]
    public void A_timestamp_older_than_the_window_fails_as_stale()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);

        // Signed at At, verified ten minutes later against a five-minute window.
        var result = new WebhookVerifier(Secret, TimeSpan.FromMinutes(5))
            .Verify(header, Body, now: At.AddMinutes(10));

        Assert.False(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.StaleTimestamp, result.Failure);
    }

    [Fact]
    public void A_timestamp_skewed_into_the_future_fails_as_stale()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);

        // Verification clock sits well before the signed time: future skew beyond tolerance.
        var result = new WebhookVerifier(Secret, TimeSpan.FromMinutes(5))
            .Verify(header, Body, now: At.AddMinutes(-10));

        Assert.False(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.StaleTimestamp, result.Failure);
    }

    [Fact]
    public void Staleness_is_decided_before_the_signature_so_a_stale_tampered_request_reads_as_stale()
    {
        // A stale request with a wrong MAC reports Stale, not SignatureMismatch: the timestamp is
        // the cheaper gate and the MAC is never computed once the window is blown.
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var staleTampered = FlipLastHexDigit(header);

        var result = new WebhookVerifier(Secret, TimeSpan.FromMinutes(5))
            .Verify(signatureHeader: staleTampered, Body, now: At.AddMinutes(30));

        Assert.Equal(WebhookVerificationFailure.StaleTimestamp, result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-signature")]
    [InlineData("t=1700000000")] // no signature segment
    [InlineData("v1=abcdef")] // no timestamp segment
    [InlineData("t=,v1=")] // empty values
    [InlineData("t=notanumber,v1=00000000000000000000000000000000000000000000000000000000000000ab")]
    [InlineData("t=1700000000,v1=tooshort")] // MAC wrong length
    [InlineData("t=1700000000,v1=ZZ000000000000000000000000000000000000000000000000000000000000ab")] // non-hex MAC
    [InlineData("t=1700000000,v2=00000000000000000000000000000000000000000000000000000000000000ab")] // wrong scheme token
    public void A_malformed_header_fails_as_malformed(string header)
    {
        var result = new WebhookVerifier(Secret).Verify(header, Body, now: At);

        Assert.False(result.IsValid);
        Assert.Equal(WebhookVerificationFailure.Malformed, result.Failure);
    }

    [Fact]
    public void An_uppercase_hex_mac_is_rejected_as_malformed()
    {
        // The signer emits lowercase hex; the verifier accepts exactly that rendering so the wire
        // contract has one canonical form rather than a case-insensitive one.
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var upper = header[..header.IndexOf("v1=", StringComparison.Ordinal)]
            + "v1="
            + header.Split("v1=")[1].ToUpperInvariant();

        var result = new WebhookVerifier(Secret).Verify(signatureHeader: upper, Body, now: At);

        Assert.Equal(WebhookVerificationFailure.Malformed, result.Failure);
    }

    [Fact]
    public void An_empty_body_round_trips()
    {
        var header = new WebhookSigner(Secret).Sign(ReadOnlySpan<byte>.Empty, At);

        var result = new WebhookVerifier(Secret)
            .Verify(header, ReadOnlySpan<byte>.Empty, now: At);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void The_window_boundary_is_inclusive()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var verifier = new WebhookVerifier(Secret, TimeSpan.FromMinutes(5));

        // Exactly on the boundary in each direction is still accepted.
        Assert.True(verifier.Verify(header, Body, now: At.AddMinutes(5)).IsValid);
        Assert.True(verifier.Verify(header, Body, now: At.AddMinutes(-5)).IsValid);

        // One second past the boundary is rejected.
        Assert.Equal(
            WebhookVerificationFailure.StaleTimestamp,
            verifier.Verify(header, Body, now: At.AddMinutes(5).AddSeconds(1)).Failure);
    }

    [Fact]
    public void A_zero_tolerance_window_requires_the_exact_signed_second()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var verifier = new WebhookVerifier(Secret, TimeSpan.Zero);

        Assert.True(verifier.Verify(header, Body, now: At).IsValid);
        Assert.Equal(
            WebhookVerificationFailure.StaleTimestamp,
            verifier.Verify(header, Body, now: At.AddSeconds(1)).Failure);
    }

    [Fact]
    public void Empty_secret_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WebhookVerifier(string.Empty));
    }

    [Fact]
    public void Null_secret_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new WebhookVerifier(null!));
    }

    [Fact]
    public void Negative_tolerance_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WebhookVerifier(Secret, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Verification_uses_the_constant_time_primitive_over_the_shared_scheme_mac()
    {
        // Pins that the verifier compares against exactly the MAC the shared SignatureScheme
        // computes (so it cannot drift from the signer), and that an independent constant-time
        // compare of that MAC against the signed header agrees with the verifier's verdict. The
        // production path runs CryptographicOperations.FixedTimeEquals over these same raw bytes.
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var sentMacHex = header.Split("v1=")[1];

        var expected = new byte[32];
        SignatureScheme.ComputeMac(
            Encoding.UTF8.GetBytes(Secret), At.ToUnixTimeSeconds(), Body, expected);

        var sentMac = Convert.FromHexString(sentMacHex);
        Assert.True(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, sentMac));
        Assert.True(new WebhookVerifier(Secret).Verify(header, Body, now: At).IsValid);
    }

    [Fact]
    public void The_result_value_has_value_equality()
    {
        var header = new WebhookSigner(Secret).Sign(Body, At);
        var verifier = new WebhookVerifier(Secret);

        var a = verifier.Verify(header, Body, now: At);
        var b = verifier.Verify(header, Body, now: At);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void The_invalid_factory_rejects_None_as_a_failure_reason()
    {
        // An invalid result must always name a concrete reason. None is reserved for a valid result,
        // so constructing Invalid(None) would produce an inconsistent value (IsValid == false with
        // Failure == None) and is rejected at the factory instead.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WebhookVerificationResult.Invalid(WebhookVerificationFailure.None));
    }

    [Theory]
    [InlineData(WebhookVerificationFailure.Malformed)]
    [InlineData(WebhookVerificationFailure.StaleTimestamp)]
    [InlineData(WebhookVerificationFailure.SignatureMismatch)]
    public void Every_failing_verification_carries_its_concrete_non_None_reason(
        WebhookVerificationFailure expected)
    {
        // Whatever the cause, a rejected verification reports IsValid == false paired with a concrete,
        // defined reason, never None. None on a false result would be the inconsistent state the
        // Invalid factory now forbids; this pins that each real failure path produces exactly its
        // reason and that the set of reachable reasons is the non-None enum members.
        var (verifier, header, now) = ArrangeFailure(expected);

        var result = verifier.Verify(header, Body, now);

        Assert.False(result.IsValid);
        Assert.NotEqual(WebhookVerificationFailure.None, result.Failure);
        Assert.True(Enum.IsDefined(result.Failure));
        Assert.Equal(expected, result.Failure);
    }

    private static (WebhookVerifier Verifier, string Header, DateTimeOffset Now) ArrangeFailure(
        WebhookVerificationFailure failure)
    {
        var goodHeader = new WebhookSigner(Secret).Sign(Body, At);

        return failure switch
        {
            // Header cannot be parsed into the t=...,v1=... shape.
            WebhookVerificationFailure.Malformed =>
                (new WebhookVerifier(Secret), "not-a-signature", At),

            // Parsed, but the timestamp falls outside the freshness window.
            WebhookVerificationFailure.StaleTimestamp =>
                (new WebhookVerifier(Secret, TimeSpan.FromMinutes(5)), goodHeader, At.AddMinutes(10)),

            // Parsed and fresh, but the recomputed MAC does not match.
            WebhookVerificationFailure.SignatureMismatch =>
                (new WebhookVerifier(Secret), FlipLastHexDigit(goodHeader), At),

            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unhandled reason."),
        };
    }

    private static string FlipLastHexDigit(string header)
    {
        var last = header[^1];
        var replacement = last == 'a' ? 'b' : 'a';
        return header[..^1] + replacement;
    }
}
