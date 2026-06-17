namespace Moongazing.OrionRelay.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionRelay.Signing;

/// <summary>
/// Measures constructing a <see cref="WebhookSigner"/> (the one-time UTF-8 decode and copy of
/// the shared secret) and a construct-then-sign round trip. This is the cost paid when a signer
/// is created per secret rather than reused, which is relevant to multi-tenant senders that hold
/// one signer per subscriber. No network or I/O is involved.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SignerConstructionBenchmarks
{
    private const string Secret = "whsec_a_representative_shared_secret_value";

    private readonly DateTimeOffset timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);
    private readonly byte[] body = BuildBody();

    /// <summary>Construct a signer over the shared secret.</summary>
    [Benchmark(Baseline = true)]
    public WebhookSigner Construct() => new(Secret);

    /// <summary>Construct a signer and immediately sign one body (cold-path delivery).</summary>
    [Benchmark]
    public string ConstructAndSign()
    {
        var signer = new WebhookSigner(Secret);
        return signer.Sign(body, timestamp);
    }

    private static byte[] BuildBody()
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(31 + (i * 17 % 223));
        }

        return bytes;
    }
}
