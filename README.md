<p align="center">
  <img src="docs/logo.png" alt="OrionRelay" width="150" />
</p>

# OrionRelay

[![CI/CD](https://github.com/tunahanaliozturk/OrionRelay/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/tunahanaliozturk/OrionRelay/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OrionRelay.svg)](https://www.nuget.org/packages/OrionRelay/)

Outbound webhook delivery for .NET. You hand it a payload and an endpoint; it signs the request,
sends it, and retries transient failures with backoff until it lands or the attempt budget runs out.

Part of the **Orion** family. Usable entirely on its own.

## Why

Delivering a webhook reliably is more than one `HttpClient.PostAsync`. You need request signing
so receivers can trust the payload, retries that distinguish a transient 503 from a permanent
400, backoff with jitter so a fleet of senders does not stampede a recovering receiver, and
telemetry so you can see delivery health. OrionRelay packages those decisions so you do not
re-derive them per project.

## Features

- **HMAC-SHA256 request signing.** Every attempt carries an `Orion-Signature` header of the form
  `t=<unix-seconds>,v1=<hex-hmac>`, with the send timestamp bound into the MAC so a receiver can
  reject replays.
- **Transient-aware retries.** Transport faults, per-attempt timeouts, and HTTP `408`/`429`/`5xx`
  are retried; any other `4xx` fails fast.
- **Equal-jitter exponential backoff.** Backoff doubles per attempt, clamps to a ceiling, and
  randomises half the delay so concurrent senders do not retry in lockstep.
- **Per-attempt telemetry.** A `System.Diagnostics.Metrics` meter exposes delivery/attempt
  counters and an attempts-per-delivery histogram, ready for OpenTelemetry.
- **Fault-safe delivery observer.** An optional hook sees every attempt and every exhausted
  delivery for dead-lettering or alerting; faults it raises never break delivery.
- **Pluggable dead-letter sink.** Deliveries that exhaust their attempt budget are routed to an
  `IDeadLetterSink` exactly once, carrying their terminal failure context, so you can persist,
  alert on, or replay them. The default is a no-op; a bounded `InMemoryDeadLetterSink` with
  oldest-first eviction is available as an opt-in, and faults the sink raises never break delivery.
- **One-call DI registration.** `AddOrionRelay` wires a dedicated `HttpClient`, the signer, the
  diagnostics, and the dispatcher, validating your options eagerly.
- **Multi-targeted.** `net8.0`, `net9.0`, and `net10.0`, with nullable enabled and warnings as errors.

## Install

```
dotnet add package OrionRelay
```

The NuGet package id is **`OrionRelay`**; the root namespace is `Moongazing.OrionRelay`.

## Quick start

Register OrionRelay with a shared signing secret and optional delivery tuning:

```csharp
using Moongazing.OrionRelay;

services.AddOrionRelay(signingSecret: "whsec_your_shared_secret", o =>
{
    o.MaxAttempts = 5;
    o.BaseDelay = TimeSpan.FromSeconds(2);
    o.MaxDelay = TimeSpan.FromMinutes(1);
});
```

Inject `IWebhookDispatcher` and send a signed payload:

```csharp
using Moongazing.OrionRelay.Delivery;

public sealed class OrderEvents(IWebhookDispatcher dispatcher)
{
    public async Task NotifyAsync(Uri subscriber, byte[] payload, CancellationToken ct)
    {
        var result = await dispatcher.DispatchAsync(new WebhookMessage
        {
            Endpoint = subscriber,
            Body = payload,
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "order.created",
        }, ct);

        if (!result.Succeeded)
        {
            // Persist for later redelivery; result.Attempts / result.StatusCode tell you why.
        }
    }
}
```

`DispatchAsync` returns when delivery succeeds (a 2xx response) or the attempt budget is exhausted.
A cancelled token aborts the whole delivery, including backoff waits, and throws
`OperationCanceledException` rather than returning a failure result.

## Usage

### Sending a webhook

`WebhookMessage` describes a single delivery:

| Property | Type | Notes |
|----------|------|-------|
| `Endpoint` | `Uri` | Required. The absolute endpoint to POST to. |
| `Body` | `ReadOnlyMemory<byte>` | Required. Transmitted verbatim and covered by the signature. |
| `ContentType` | `string` | Defaults to `application/json`. Sets the request `Content-Type`. |
| `EventId` | `string?` | Optional. Sent as the `Orion-Event-Id` header for receiver-side deduplication. |
| `EventType` | `string?` | Optional. Sent as the `Orion-Event-Type` header and used for the `event_type` telemetry tag. |

`WebhookDeliveryResult` reports the outcome:

| Member | Type | Notes |
|--------|------|-------|
| `Succeeded` | `bool` | True when a 2xx response arrived within the attempt budget. |
| `Attempts` | `int` | Attempts made, including the first send. |
| `StatusCode` | `int?` | Last HTTP status observed, or null if every attempt failed at the transport level. |
| `FinalException` | `Exception?` | The final transport fault, when delivery ended on one rather than an HTTP error. |

### Signature verification on the receiver

When a signing secret is configured, every request carries an `Orion-Signature` header of the form
`t=<unix-seconds>,v1=<hex-hmac>`. The HMAC-SHA256 is taken over `<unix-seconds>.<body>`, so the
timestamp is bound into the MAC. A receiver verifies by recomputing the MAC over the exact raw body
it received and rejecting requests whose timestamp falls outside a freshness window, which stops
replays. The library ships the sender-side `WebhookSigner`; the receiver below is illustrative and
uses only the same HMAC contract.

```csharp
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public static bool VerifySignature(
    string signatureHeader, byte[] rawBody, string secret, TimeSpan tolerance)
{
    // Header: "t=<unix-seconds>,v1=<hex-hmac>"
    var parts = signatureHeader.Split(',');
    var timestamp = parts[0]["t=".Length..];
    var sentMac = parts[1]["v1=".Length..];

    var unixSeconds = long.Parse(timestamp, CultureInfo.InvariantCulture);
    var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    if (DateTimeOffset.UtcNow - sentAt > tolerance)
    {
        return false; // outside the freshness window: reject as a possible replay
    }

    var signed = Encoding.UTF8.GetBytes($"{unixSeconds}.")
        .Concat(rawBody)
        .ToArray();
    var expected = Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed)).ToLowerInvariant();

    // Constant-time compare to avoid leaking the MAC through timing.
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(sentMac));
}
```

Verify against the raw bytes exactly as received, before any deserialization reshapes them.
The sender side of this contract is `IWebhookSigner.Sign(ReadOnlySpan<byte> body, DateTimeOffset timestamp)`.

### Retries and backoff

An attempt is retried when it produces a transport fault, a per-attempt timeout, or an HTTP
`408`, `429`, or `5xx`. Any other `4xx` is treated as permanent and fails fast. Backoff is
exponential from `BaseDelay`, doubled per attempt and clamped to `MaxDelay`, with equal jitter
(half the computed delay kept as a floor, the other half randomised) so concurrent senders do not
retry in lockstep. The dispatcher enforces a per-attempt timeout (`RequestTimeout`) independently
of the `HttpClient`, which `AddOrionRelay` leaves uncapped so the two do not race.

### Delivery observer hook

Implement `IWebhookDeliveryObserver` and register it in DI before resolving the dispatcher to see
every attempt and every exhausted delivery, for dead-lettering, alerting, or audit:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Observers;

public sealed class DeadLetterObserver(IDeadLetterStore store) : IWebhookDeliveryObserver
{
    public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception)
    {
        // Per-attempt visibility: log, count, trace.
    }

    public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result)
    {
        // The attempt budget ran out. Park the message for later redelivery.
        store.Park(message, result);
    }
}

// Register the observer before AddOrionRelay resolves the dispatcher.
services.AddSingleton<IWebhookDeliveryObserver, DeadLetterObserver>();
services.AddOrionRelay(signingSecret: "whsec_your_shared_secret");
```

The observer is observability only. The dispatcher swallows any exception it raises, so an observer
outage never breaks delivery. If you register none, a no-op (`NullWebhookDeliveryObserver`) is used.

### Dead-letter sink

When a delivery exhausts its attempt budget, the dispatcher routes it to an `IDeadLetterSink`
exactly once, after the final failed attempt, so a consumer can persist, alert on, or replay it.
The sink receives a `DeadLetterEntry` carrying the original message, the terminal
`WebhookDeliveryResult`, and the instant the delivery was abandoned:

| Member | Type | Notes |
|--------|------|-------|
| `Message` | `WebhookMessage` | The message that could not be delivered within its attempt budget. |
| `Result` | `WebhookDeliveryResult` | The terminal failure result: attempts made, last status, and final transport fault. |
| `DeadLetteredAt` | `DateTimeOffset` | The instant the delivery was abandoned and routed to the sink. |

`AddOrionRelay` registers the no-op `NullDeadLetterSink` by default, which retains nothing and so
cannot grow the process working set during a prolonged receiver outage. Register your own sink
before the dispatcher resolves to capture abandoned deliveries instead. A durable store is the
right choice for production; for tests, demos, and single-process apps the library ships a bounded
`InMemoryDeadLetterSink` that retains the most recent entries in arrival order and evicts the
oldest once full:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionRelay;
using Moongazing.OrionRelay.Delivery;

// Opt in to the in-memory sink, bounded to the 256 most recent abandoned deliveries.
services.AddSingleton<IDeadLetterSink>(new InMemoryDeadLetterSink(capacity: 256));
services.AddOrionRelay(signingSecret: "whsec_your_shared_secret");
```

The capacity argument is optional; the parameterless constructor retains up to
`InMemoryDeadLetterSink.DefaultCapacity` (1024) entries. Inspect what has been captured through
`Count` and the oldest-first `Entries` snapshot:

```csharp
public sealed class FailedDeliveryReport(IDeadLetterSink sink)
{
    public void Print()
    {
        if (sink is not InMemoryDeadLetterSink inMemory)
        {
            return;
        }

        foreach (var entry in inMemory.Entries)
        {
            Console.WriteLine(
                $"{entry.DeadLetteredAt:o} {entry.Message.EventType} " +
                $"failed after {entry.Result.Attempts} attempts " +
                $"(last status: {entry.Result.StatusCode?.ToString() ?? "none"})");
        }
    }
}
```

To persist or replay instead, implement `IDeadLetterSink` over your own store:

```csharp
using Moongazing.OrionRelay.Delivery;

public sealed class DurableDeadLetterSink(IDeadLetterStore store) : IDeadLetterSink
{
    public async Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default)
    {
        // Persist for later inspection or redelivery. Keep this resilient: the dispatcher swallows
        // any fault raised here, so a sink outage cannot turn an already-failed delivery into an
        // exception for the caller.
        await store.SaveAsync(entry, cancellationToken);
    }
}

services.AddSingleton<IDeadLetterSink, DurableDeadLetterSink>();
services.AddOrionRelay(signingSecret: "whsec_your_shared_secret");
```

The sink and the delivery observer are complementary: `IWebhookDeliveryObserver.OnExhausted` fires
first for in-process observability, then the entry is written to the sink for durable capture.

## Configuration

`WebhookDeliveryOptions` is configured through the `AddOrionRelay` callback and validated eagerly at
registration (an invalid combination throws `ArgumentOutOfRangeException` there, not at first send):

| Option | Type | Default | Meaning |
|--------|------|---------|---------|
| `MaxAttempts` | `int` | `4` | Total attempts including the first send. Must be at least 1. |
| `BaseDelay` | `TimeSpan` | `1s` | Base backoff delay, doubled each retry. Cannot be negative. |
| `MaxDelay` | `TimeSpan` | `30s` | Ceiling the exponential backoff is clamped to. Cannot be less than `BaseDelay`. |
| `RequestTimeout` | `TimeSpan` | `30s` | Per-attempt HTTP timeout, enforced by the dispatcher. |
| `SignatureHeader` | `string` | `Orion-Signature` | Header name carrying the signature value. |

Pass the signing secret as the first argument to `AddOrionRelay`. A null or empty secret registers
no signer and sends unsigned, which is not recommended outside trusted networks.

## Telemetry

`WebhookDiagnostics` exposes a `System.Diagnostics.Metrics` meter named **`Moongazing.OrionRelay`**
(also available as the `WebhookDiagnostics.MeterName` constant). Subscribe to it from OpenTelemetry
or any `MeterListener`:

| Instrument | Kind | Tags |
|------------|------|------|
| `orionrelay.deliveries` | Counter&lt;long&gt; | `outcome` (`succeeded`/`failed`), `event_type` |
| `orionrelay.attempts` | Counter&lt;long&gt; | `outcome` (`success`/`retryable`/`fatal`) |
| `orionrelay.delivery.attempts` | Histogram&lt;int&gt; | `event_type` |

`orionrelay.deliveries` counts one per `DispatchAsync` call; `orionrelay.attempts` counts each
individual HTTP attempt; the histogram records how many attempts each delivery took. With
OpenTelemetry:

```csharp
using Moongazing.OrionRelay.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(WebhookDiagnostics.MeterName));
```

The diagnostics instance is registered as a singleton by `AddOrionRelay`.

## Testing

The dispatcher is built for deterministic testing. An internal constructor accepts seams for the
backoff delay, jitter source, and clock, so tests can run the retry loop with zero real waits, a
fixed jitter, and a fixed `now`. The signer is deterministic for a given secret, body, and
timestamp. The suite covers signing (envelope shape, determinism, timestamp and secret sensitivity,
empty-secret rejection), delivery (first-attempt success, retry-then-success, fail-fast on `4xx`,
budget exhaustion, transport-fault retry, signing, caller cancellation, observer-fault isolation),
and DI registration.

```
dotnet test
```

Microbenchmarks for the CPU-bound send-path work (signing, signer construction, telemetry emission)
live under `benchmarks/` and run with BenchmarkDotNet. See [benchmarks.md](benchmarks.md).

## Versioning

OrionRelay follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The current release
is `0.2.0`; while on the `0.x` line the public surface may still change between minor versions.
Notable changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## Design notes

- Multi-targets `net8.0`, `net9.0`, `net10.0`.
- `TreatWarningsAsErrors`, latest analyzers, nullable enabled.
- The dispatcher enforces its own per-attempt timeout, so its `HttpClient` is left uncapped.

## Documentation

- [docs/FEATURES.md](docs/FEATURES.md) - the public surface, feature by feature.
- [docs/ROADMAP.md](docs/ROADMAP.md) - directions under consideration (ideas, not promises).
- [benchmarks.md](benchmarks.md) - what the benchmark suite measures and how to run it.
- [CHANGELOG.md](CHANGELOG.md) - notable changes per release.

## More from the Orion family

OrionRelay is one of a set of standalone .NET libraries by the same author. See
[OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) and the other Orion packages.

## Contributing

Issues and pull requests welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) and the
[Code of Conduct](CODE_OF_CONDUCT.md) before opening one.

## License

This project is licensed under the [MIT License](LICENSE).

## Author

**Tunahan Ali Ozturk** - [GitHub](https://github.com/tunahanaliozturk)
</content>
</invoke>
