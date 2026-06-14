# OrionRelay

[![CI/CD](https://github.com/tunahanaliozturk/OrionRelay/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/tunahanaliozturk/OrionRelay/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OrionRelay.svg)](https://www.nuget.org/packages/OrionRelay/)

Outbound webhook delivery for .NET. You hand it a payload and an endpoint; it signs the request,
sends it, and retries transient failures with backoff until it lands or the budget runs out.

Part of the **Orion** family. Usable entirely on its own.

## Why

Delivering a webhook reliably is more than one `HttpClient.PostAsync`. You need request signing
so receivers can trust the payload, retries that distinguish a transient 503 from a permanent
400, backoff with jitter so a fleet of senders does not stampede a recovering receiver, and
telemetry so you can see delivery health. OrionRelay packages those decisions so you do not
re-derive them per project.

## Install

```
dotnet add package OrionRelay
```

## Quick start

```csharp
services.AddOrionRelay(signingSecret: "whsec_your_shared_secret", o =>
{
    o.MaxAttempts = 5;
    o.BaseDelay = TimeSpan.FromSeconds(2);
    o.MaxDelay = TimeSpan.FromMinutes(1);
});
```

```csharp
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

## What it does

### Signing

When you supply a signing secret, every attempt carries an `Orion-Signature` header of the form
`t=<unix-seconds>,v1=<hex-hmac>`. The HMAC-SHA256 is taken over `<unix-seconds>.<body>`, so the
timestamp is bound into the signature. A receiver verifies by recomputing the MAC and rejecting
requests whose timestamp is outside its freshness window, which stops replays.

### Retry and backoff

An attempt is retried when it produces a transport fault, a request timeout, or an HTTP
`408`, `429`, or `5xx`. Any other `4xx` is treated as permanent and fails fast. Backoff is
exponential from `BaseDelay`, doubled per attempt and clamped to `MaxDelay`, with equal jitter
(half the computed delay as a floor, the other half randomised) so concurrent senders do not
retry in lockstep.

### Telemetry

Subscribe to the `Moongazing.OrionRelay` meter:

| Instrument | Kind | Tags |
|------------|------|------|
| `orionrelay.deliveries` | Counter | `outcome` (succeeded/failed), `event_type` |
| `orionrelay.attempts` | Counter | `outcome` (success/retryable/fatal) |
| `orionrelay.delivery.attempts` | Histogram | `event_type` |

### Delivery observer

Implement `IWebhookDeliveryObserver` and register it before the dispatcher to see every attempt
and every exhausted delivery (for dead-lettering, alerting, or audit). It is observability only:
the dispatcher swallows any fault it raises, so an observer outage never breaks delivery.

## Design

- Multi-targets `net8.0`, `net9.0`, `net10.0`.
- `TreatWarningsAsErrors`, latest analyzers, nullable enabled.
- The dispatcher enforces its own per-attempt timeout, so its `HttpClient` is left uncapped.

## License

MIT.
