<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionRelay are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-06-19

### Added

- `IDeadLetterSink`: deliveries that exhaust their attempt budget are routed to a pluggable sink
  exactly once, carrying a `DeadLetterEntry` with the original message, the terminal
  `WebhookDeliveryResult`, and the abandonment timestamp. Sink faults are swallowed so an outage
  cannot turn an already-failed delivery into a thrown exception.
- `NullDeadLetterSink`: the no-op sink registered by default. It retains nothing, so a prolonged
  receiver outage cannot grow the process working set by accumulating abandoned deliveries.
- `InMemoryDeadLetterSink`: opt-in in-memory capture, bounded by a fixed `Capacity`
  (`DefaultCapacity` = 1024) with documented oldest-first eviction once full. Entries are lost on
  restart; register a durable sink for production.
- `WebhookDispatcher` gains a sink-aware constructor overload accepting an `IDeadLetterSink`. The
  original v0.1.0 five-argument constructor is preserved as a distinct overload (delegating to the
  no-op sink), so assemblies compiled against 0.1.0 remain binary-compatible.
- `AddOrionRelay()` wires `NullDeadLetterSink` by default; register your own sink (for example a
  bounded `InMemoryDeadLetterSink` or a durable store) before the dispatcher resolves to capture
  abandoned deliveries.

### Tests

- Added coverage for dead-letter routing (exhausted, non-retryable, and successful deliveries;
  sink-fault isolation; arrival order), the bounded in-memory sink (oldest-first eviction past
  capacity, non-positive-capacity guard), the restored five-argument constructor routing to a
  no-op without throwing, and the safe no-op registration default plus opt-in override. Also a
  regression test locking `IWebhookDeliveryObserver.OnExhausted` to exactly one call, after the
  final failed attempt, carrying the terminal result.

## [0.1.0] - 2026-06-14

### Added

Initial release. Outbound webhook delivery.

- `IWebhookDispatcher` / `WebhookDispatcher`: signs each attempt, retries transport faults,
  request timeouts, and HTTP 408/429/5xx with equal-jitter exponential backoff, and stops on
  success, a non-retryable status, or an exhausted attempt budget.
- `IWebhookSigner` / `WebhookSigner`: HMAC-SHA256 request signing with the timestamp bound into
  the signature (`t=<unix>,v1=<hmac>`) for replay rejection.
- `WebhookDeliveryOptions`: attempt budget, base/max backoff, per-attempt timeout, signature
  header name; validated on registration.
- `IWebhookDeliveryObserver`: fault-safe hook for per-attempt and exhausted-delivery events.
- `WebhookDiagnostics`: `Moongazing.OrionRelay` meter with delivery/attempt counters and an
  attempts-per-delivery histogram.
- `AddOrionRelay()` DI extension wiring a dedicated `HttpClient`, the signer, and the dispatcher.

### Tests

18 tests across signing, delivery (success, retry, fatal, exhaustion, transport fault,
cancellation, observer fault isolation), and registration.

[0.2.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.1.0
