# Benchmarks

Microbenchmarks for OrionRelay's in-memory, CPU-bound delivery hot paths, built with
[BenchmarkDotNet](https://benchmarkdotnet.org/). They cover the compute the library does on the
send path: HMAC-SHA256 request signing, signer construction, and per-attempt telemetry emission.

These benchmarks make no network calls and touch no database. They exercise only the public API of
`Moongazing.OrionRelay` (`WebhookSigner` and `WebhookDiagnostics`), so the numbers reflect pure
CPU and allocation cost, not transport latency. The actual HTTP send, retry loop, and backoff
delays are deliberately excluded because they are dominated by I/O and wall-clock waits rather than
computation.

## Benchmark classes

| Class | What it measures |
|-------|------------------|
| `SigningBenchmarks` | `WebhookSigner.Sign` over payloads of 64 B, 1 KiB, and 64 KiB: timestamp-prefixing the body, computing the HMAC-SHA256 MAC, and formatting the `t=<unix-seconds>,v1=<hex>` header value. This is the work done on every signed delivery attempt. |
| `SignerConstructionBenchmarks` | Constructing a `WebhookSigner` (the one-time UTF-8 decode and copy of the shared secret), and a construct-then-sign round trip. Relevant to multi-tenant senders that hold one signer per subscriber rather than reusing a single instance. |
| `TelemetryBenchmarks` | The per-attempt and per-completion metric emission against `WebhookDiagnostics`: counter increments with `outcome`/`event_type` tags and the attempts-per-delivery histogram record, including the tag boxing into `KeyValuePair<string, object?>`. Measured with no listener attached, i.e. the unsubscribed cost paid on every send. |

## Running

Run the whole suite (Release is required by BenchmarkDotNet):

```
dotnet run -c Release --project benchmarks/Moongazing.OrionRelay.Benchmarks
```

Filter to one class or benchmark with the BenchmarkDotNet switcher:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionRelay.Benchmarks -- --filter "*SigningBenchmarks*"
dotnet run -c Release --project benchmarks/Moongazing.OrionRelay.Benchmarks -- --filter "*Sign*"
```

List the available benchmarks without running them:

```
dotnet run -c Release --project benchmarks/Moongazing.OrionRelay.Benchmarks -- --list flat
```

## Runtimes

Each class is annotated with `[SimpleJob(RuntimeMoniker.Net80)]` and
`[SimpleJob(RuntimeMoniker.Net90)]`, so a full run compares the same code on .NET 8 and .NET 9.
Both SDKs must be installed for a complete run; pass `--runtimes net80` or `--runtimes net90` to
restrict to one. All classes carry `[MemoryDiagnoser]`, so allocation per operation is reported
alongside time.

## Results

No results are committed. Run the suite locally on the hardware and runtime you care about;
absolute numbers vary by machine and are only meaningful relative to each other on the same host.
