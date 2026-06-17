# OrionRelay Features

A feature-by-feature tour of the public surface of `Moongazing.OrionRelay` (NuGet id `OrionRelay`).
Everything here reflects the shipped `0.1.0` API. Internals such as the backoff calculation and the
HTTP request construction are private and are described only by their observable behaviour.

---

## Table of contents

1. [Webhook dispatch](#1-webhook-dispatch)
2. [Request signing](#2-request-signing)
3. [Retries and backoff](#3-retries-and-backoff)
4. [Delivery options](#4-delivery-options)
5. [Delivery result](#5-delivery-result)
6. [Delivery observer](#6-delivery-observer)
7. [Telemetry](#7-telemetry)
8. [Dependency injection](#8-dependency-injection)

---

## 1. Webhook dispatch

`IWebhookDispatcher` delivers a single `WebhookMessage` and reports the outcome.

```csharp
Task<WebhookDeliveryResult> DispatchAsync(
    WebhookMessage message, CancellationToken cancellationToken = default);
```

- Returns when delivery succeeds (a 2xx response) or the attempt budget is exhausted.
- A cancelled `cancellationToken` aborts the whole delivery, including backoff waits, and throws
  `OperationCanceledException` rather than returning a failure result.
- Each attempt POSTs the message body to `WebhookMessage.Endpoint` with the configured content type.

`WebhookMessage` carries the payload and routing metadata:

| Property | Type | Required | Notes |
|----------|------|----------|-------|
| `Endpoint` | `Uri` | Yes | Absolute endpoint to POST to. |
| `Body` | `ReadOnlyMemory<byte>` | Yes | Transmitted verbatim; covered by the signature. |
| `ContentType` | `string` | No | Defaults to `application/json`. |
| `EventId` | `string?` | No | Sent as the `Orion-Event-Id` header for receiver-side deduplication. |
| `EventType` | `string?` | No | Sent as the `Orion-Event-Type` header; also tags telemetry. |

---

## 2. Request signing

`IWebhookSigner` / `WebhookSigner` produce the signature header a receiver uses to verify a request.

```csharp
string Sign(ReadOnlySpan<byte> body, DateTimeOffset timestamp);
```

- HMAC-SHA256 over `<unix-seconds>.<body>`, so the send timestamp is bound into the MAC.
- Returns a header value of the form `t=<unix-seconds>,v1=<hex-hmac>` (lowercase hex).
- The secret is provided once at construction and never leaves the instance; an empty secret is
  rejected with `ArgumentException`.
- The dispatcher adds the value under the header named by `WebhookDeliveryOptions.SignatureHeader`
  (default `Orion-Signature`) when a signer is configured.

Because the timestamp is part of the signed input, a receiver that enforces a freshness window can
reject captured-and-replayed requests. The receiver recomputes the MAC over the raw body it received
and compares in constant time. See the README "Signature verification on the receiver" section for a
worked receiver-side example.

---

## 3. Retries and backoff

The dispatcher classifies each attempt and retries only the transient ones:

| Attempt outcome | Classification |
|-----------------|----------------|
| 2xx response | Success, stop |
| HTTP 408, 429, or 5xx | Retryable |
| Transport fault (`HttpRequestException`) | Retryable |
| Per-attempt timeout (not caller cancellation) | Retryable |
| Any other 4xx | Fatal, stop immediately |

Backoff between retries is exponential from `BaseDelay`, doubled per attempt and clamped to
`MaxDelay`, with equal jitter: half the computed delay is kept as a floor and the other half is
randomised, so a fleet of senders retrying the same recovering receiver does not synchronise. Each
attempt is also bounded by `RequestTimeout`, enforced by the dispatcher independently of the
`HttpClient` timeout.

---

## 4. Delivery options

`WebhookDeliveryOptions` is validated eagerly at registration; an invalid combination throws
`ArgumentOutOfRangeException` there rather than at first send.

| Option | Type | Default | Constraint |
|--------|------|---------|------------|
| `MaxAttempts` | `int` | `4` | At least 1. |
| `BaseDelay` | `TimeSpan` | `1s` | Not negative. |
| `MaxDelay` | `TimeSpan` | `30s` | Not less than `BaseDelay`. |
| `RequestTimeout` | `TimeSpan` | `30s` | Per-attempt HTTP timeout. |
| `SignatureHeader` | `string` | `Orion-Signature` | Header name for the signature value. |

---

## 5. Delivery result

`WebhookDeliveryResult` is immutable and describes the terminal outcome of a dispatch:

| Member | Type | Notes |
|--------|------|-------|
| `Succeeded` | `bool` | True when a 2xx arrived within the attempt budget. |
| `Attempts` | `int` | Attempts made, including the first send. |
| `StatusCode` | `int?` | Last HTTP status observed, or null if every attempt failed at the transport level. |
| `FinalException` | `Exception?` | Final transport fault, when delivery ended on one rather than an HTTP error. |

---

## 6. Delivery observer

`IWebhookDeliveryObserver` is an optional, fault-safe hook:

```csharp
void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception);
void OnExhausted(WebhookMessage message, WebhookDeliveryResult result);
```

- `OnAttempt` fires after every individual HTTP attempt, success or failure.
- `OnExhausted` fires once when a delivery is abandoned after exhausting its attempt budget.
- It is observability only. The dispatcher swallows any exception an observer raises, so an observer
  outage can never break delivery.
- Register one in DI before resolving the dispatcher. If none is registered, the built-in
  `NullWebhookDeliveryObserver` no-op is used.

Typical uses: dead-lettering exhausted deliveries, alerting, and per-attempt audit logging.

---

## 7. Telemetry

`WebhookDiagnostics` owns a `System.Diagnostics.Metrics` meter named `Moongazing.OrionRelay`
(exposed as `WebhookDiagnostics.MeterName`):

| Instrument | Kind | Tags |
|------------|------|------|
| `orionrelay.deliveries` | `Counter<long>` | `outcome` (`succeeded`/`failed`), `event_type` |
| `orionrelay.attempts` | `Counter<long>` | `outcome` (`success`/`retryable`/`fatal`) |
| `orionrelay.delivery.attempts` | `Histogram<int>` | `event_type` |

The instance is `IDisposable` (disposing it releases the meter) and is registered as a singleton by
`AddOrionRelay`. Any OpenTelemetry `MeterProvider` or raw `MeterListener` can subscribe by meter name.

---

## 8. Dependency injection

`AddOrionRelay` is the one-call entry point:

```csharp
IServiceCollection AddOrionRelay(
    this IServiceCollection services,
    string? signingSecret = null,
    Action<WebhookDeliveryOptions>? configure = null);
```

It registers the validated options, the singleton `WebhookDiagnostics`, a signer when a non-empty
`signingSecret` is supplied, a dedicated named `HttpClient` (left uncapped so the dispatcher's
per-attempt timeout governs), and the `IWebhookDispatcher` itself. An `IWebhookDeliveryObserver`
registered before the dispatcher is resolved is picked up automatically. Registrations use
`TryAdd*`, so your own prior registrations of any of these services win.
</content>
