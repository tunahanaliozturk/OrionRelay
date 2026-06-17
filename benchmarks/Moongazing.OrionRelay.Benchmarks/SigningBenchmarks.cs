namespace Moongazing.OrionRelay.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

using Moongazing.OrionRelay.Signing;

/// <summary>
/// Measures the per-attempt HMAC-SHA256 request signing hot path
/// (<see cref="WebhookSigner.Sign"/>) across representative payload sizes.
/// This is the work done on every delivery attempt when a signing secret is configured:
/// prefixing the body with the send timestamp, computing the MAC, and formatting the
/// <c>t=&lt;unix-seconds&gt;,v1=&lt;hex&gt;</c> header value. No network or I/O is involved.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SigningBenchmarks
{
    private readonly WebhookSigner signer = new("whsec_a_representative_shared_secret_value");
    private readonly DateTimeOffset timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);
    private byte[] body = [];

    /// <summary>Payload size in bytes: tiny event, typical JSON, and a large document.</summary>
    [Params(64, 1024, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        body = new byte[PayloadBytes];
        // Fill with non-zero, varied bytes so the hash work is representative.
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(31 + (i * 17 % 223));
        }
    }

    /// <summary>Sign a body of the configured size, returning the header value.</summary>
    [Benchmark]
    public string Sign() => signer.Sign(body, timestamp);
}
