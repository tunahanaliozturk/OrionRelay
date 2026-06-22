<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionRelay are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-06-22

### Added

- `IWebhookVerifier` / `WebhookVerifier`: a first-class receiver-side verifier, the counterpart to
  `WebhookSigner`. It recomputes the HMAC-SHA256 over the same canonical `<unix-seconds>.<body>`
  preimage the signer uses, enforces a configurable freshness window on the bound timestamp to
  reject replays and clock-skewed requests in either direction, and compares signatures in constant
  time via `CryptographicOperations.FixedTimeEquals`. Receivers no longer need to copy the
  illustrative HMAC check from the README.
- `WebhookVerificationResult` / `WebhookVerificationFailure`: `Verify` returns a value, not an
  exception, carrying whether the signature is valid and, when not, the single specific reason
  (`Malformed`, `StaleTimestamp`, or `SignatureMismatch`) so a receiver can branch per cause. The
  freshness window is set per verifier instance and defaults to `WebhookVerifier.DefaultTolerance`
  (5 minutes); the timestamp is gated before the MAC is computed.
- `SignatureScheme` (internal): the wire-format tokens and the canonical preimage now live in one
  place that both the signer and the verifier compute against, so the two sides cannot drift. The
  signer was refactored onto it with no change to the emitted bytes; the `t=<unix>,v1=<hex>` wire
  format is unchanged and existing signatures verify as before.

### Tests

- Added round-trip coverage that verifies signatures produced by the actual `WebhookSigner`
  (valid signature accepted; tampered body, wrong secret, and flipped signature byte rejected as
  `SignatureMismatch`; timestamps outside the window in either direction rejected as
  `StaleTimestamp`; inclusive window boundary; zero-tolerance exact-second window; malformed headers
  rejected as `Malformed`, including a wrong-length, non-hex, uppercase-hex, wrong-scheme-token, or
  multi-segment MAC; empty-body round trip; staleness decided before the signature; constant-time
  comparison over the shared-scheme MAC). The signer's existing wire-contract tests continue to pin
  byte-for-byte output after the refactor.

### Notes

- The durable dead-letter store referenced for `0.3.0` on the roadmap is still planned: it would
  introduce a new persistence package and is out of scope for this release, which ships the verifier
  into the existing core package only.

## [0.2.2] - 2026-06-20

### Performance

- `WebhookSigner.Sign` no longer allocates a separate preimage byte array per call. The
  `"<unix-seconds>.<body>"` preimage is assembled into a pooled buffer (cleared on return) and the
  signature hex is written directly in lowercase rather than allocating an uppercase string and then
  lowercasing it. The signature wire format is byte-for-byte identical. On the per-delivery signing
  path this cuts allocations from a body-size-dependent ~680 B (64 B body) / ~1640 B (1 KB body) to a
  constant 184 B, with a measured 8 to 11 percent throughput improvement.

## [0.2.1] - 2026-06-20

### Changed

- The `System.Diagnostics.Metrics.Meter` version now derives from the package version
  (`AssemblyInformationalVersionAttribute`) instead of a hardcoded literal, so the telemetry
  version tracks the package automatically and no longer drifts behind a stale string.

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

[0.3.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.3.0
[0.2.2]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.2
[0.2.1]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.1
[0.2.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.1.0
