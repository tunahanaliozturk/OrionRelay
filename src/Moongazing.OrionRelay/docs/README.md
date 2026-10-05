# OrionRelay

Outbound webhook delivery for .NET: HMAC-SHA256 request signing, retries of transient failures with
equal-jitter exponential backoff, a dead-letter sink for deliveries that fail for good, a
receiver-side verifier, and OpenTelemetry-ready metrics.

![WebhookDispatcher flow: sign and POST, 2xx delivers, 408/429/5xx or a transport fault retries after backoff while attempts remain, any other 4xx or a spent budget calls OnExhausted, writes the dead-letter entry and returns a failed result](https://raw.githubusercontent.com/tunahanaliozturk/OrionRelay/main/docs/diagrams/dispatch-retry.png)

## Install

    dotnet add package OrionRelay

Targets `net8.0`, `net9.0` and `net10.0`. The root namespace is `Moongazing.OrionRelay`.

## Quick start

```csharp
using Moongazing.OrionRelay;
using Moongazing.OrionRelay.Delivery;

services.AddOrionRelay(signingSecret: "whsec_your_shared_secret", o =>
{
    o.MaxAttempts = 5;
    o.BaseDelay = TimeSpan.FromSeconds(2);
    o.MaxDelay = TimeSpan.FromMinutes(1);
});

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
            // result.Attempts, result.StatusCode and result.FinalException say why.
        }
    }
}
```

`DispatchAsync` returns a `WebhookDeliveryResult` when the delivery succeeds (2xx), a non-retryable
status ends it, or the attempt budget is spent. Caller cancellation throws
`OperationCanceledException` instead.

## Options

`WebhookDeliveryOptions`, validated when `AddOrionRelay` runs (an invalid value throws
`ArgumentOutOfRangeException` there, not at first send):

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxAttempts` | `4` | Total attempts including the first send. At least 1. |
| `BaseDelay` | `1s` | Backoff base, doubled each retry. Not negative. |
| `MaxDelay` | `30s` | Backoff ceiling. Not less than `BaseDelay`. |
| `RequestTimeout` | `30s` | Per-attempt timeout, enforced by the dispatcher. |
| `SignatureHeader` | `Orion-Signature` | Header carrying the signature. |

Retried: transport faults, per-attempt timeouts, HTTP `408`, `429` and `5xx`. Any other `4xx` stops
at once. A null or empty signing secret sends unsigned.

## Signing and verification

Each attempt carries `Orion-Signature: t=<unix-seconds>,v1=<hex-hmac>`, the HMAC-SHA256 of
`<unix-seconds>.<body>`. On the receiver, verify the raw body bytes:

```csharp
using Moongazing.OrionRelay.Signing;

var verifier = new WebhookVerifier(secret, tolerance: TimeSpan.FromMinutes(5));
var check = verifier.Verify(signatureHeader, rawBody, DateTimeOffset.UtcNow);
if (!check.IsValid)
{
    // check.Failure is Malformed, StaleTimestamp or SignatureMismatch.
}
```

`Verify` never throws for a bad request; it compares in constant time and rejects timestamps outside
the tolerance (default `WebhookVerifier.DefaultTolerance`, 5 minutes) in either direction.

## Failed deliveries

A delivery that ends without success (budget spent or a non-retryable status) is reported once to
`IWebhookDeliveryObserver.OnExhausted`, then written once to `IDeadLetterSink` as a
`DeadLetterEntry`. Faults raised by either are swallowed, so they never break delivery.

- Default sink: `NullDeadLetterSink`, keeps nothing.
- `InMemoryDeadLetterSink`: opt-in, bounded (`DefaultCapacity` 1024), evicts the oldest first.
- Durable: the `OrionRelay.EntityFrameworkCore` package.

```csharp
services.AddSingleton<IDeadLetterSink>(new InMemoryDeadLetterSink(capacity: 256));
services.AddOrionRelay(signingSecret: "whsec_your_shared_secret");
```

## Telemetry

Meter `Moongazing.OrionRelay` (`WebhookDiagnostics.MeterName`):

| Instrument | Kind | Tags |
|------------|------|------|
| `orion.relay.deliveries` | Counter | `orion.outcome` (succeeded/failed), `event_type` |
| `orion.relay.attempts` | Counter | `orion.outcome` (success/retryable/fatal) |
| `orion.relay.delivery.attempts` | Histogram | `event_type` |

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(WebhookDiagnostics.MeterName));
```

## NativeAOT

CI publishes a NativeAOT smoke test of the sign and verify round trip with warnings as errors. The
package does not declare `IsAotCompatible` yet.

## Related packages

- `OrionRelay.EntityFrameworkCore` - durable EF Core dead-letter sink for abandoned deliveries.
- `Orion.Abstractions` - the family's shared telemetry conventions, referenced by this package.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionRelay
- Changelog: https://github.com/tunahanaliozturk/OrionRelay/blob/main/CHANGELOG.md
- License: MIT
