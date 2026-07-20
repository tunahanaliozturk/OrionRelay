<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionRelay are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security

- **Pinned `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.12 to clear GHSA-2m69-gcr7-jv3q (High).** The
  advisory affects the bundled SQLite native library, which
  `Microsoft.EntityFrameworkCore.Sqlite` -> `Microsoft.Data.Sqlite` -> `SQLitePCLRaw.bundle_e_sqlite3`
  resolved transitively at 2.1.6 (net8.0), 2.1.10 (net9.0) and 2.1.11 (net10.0). Pinning the bundle
  lifts `SQLitePCLRaw.core`, `lib.e_sqlite3` and `provider.e_sqlite3` to the patched 2.1.12 on every
  target framework.
- **No shipped or released version of OrionRelay is affected.** The vulnerable package reached only
  `Moongazing.OrionRelay.EntityFrameworkCore.Tests`, a non-packable test project that uses SQLite to
  exercise real relational constraints. The published `OrionRelay.EntityFrameworkCore` package
  references `Microsoft.EntityFrameworkCore.Relational` only — it pulls in no SQLite provider and no
  SQLitePCLRaw — so no consumer of any released version ever received the affected library. Nothing
  needs to be upgraded downstream.
- Removed the `NU1903` suppression from the test project. It was added when the advisory had no fixed
  version on the feed; 2.1.12 publishes the fix, so the NuGet audit runs unsuppressed there again and
  will fail the build on any future advisory rather than silently absorbing it.

## [0.4.0] - 2026-07-20

### Added

- **New package `OrionRelay.EntityFrameworkCore`.** A durable `IDeadLetterSink` reference
  implementation over a relational table via Entity Framework Core, so webhook deliveries that
  exhaust their attempt budget survive a process restart instead of being lost with the
  in-memory-only sink. It is a new persistence package, not a change to the core: the
  `IDeadLetterSink` interface is unchanged. The package references
  `Microsoft.EntityFrameworkCore.Relational` only, so the consumer chooses the database provider.
- `EntityFrameworkCoreDeadLetterSink<TContext>`: persists each `DeadLetterEntry` the dispatcher
  routes to it as a `DeadLetterRecord` carrying the target endpoint, the payload, the content type
  and event headers, the attempt count, the last HTTP status, the final transport error message, and
  the abandonment timestamp. The write is idempotent on the delivery id (the message `EventId` when
  set, otherwise a surrogate): `DeliveryId` is the primary key, so a re-routed replayed terminal
  delivery updates the existing row rather than inserting a duplicate, and a concurrent first insert
  is reconciled by re-reading the row rather than by sniffing a provider-specific SQL error code.
- `DeadLetterRecord`, `DeadLetterRecordConfiguration`, and `OrionRelayDeadLetterDbContext`: the
  mapped entity, its relational mapping (keyed by `DeliveryId`, with the abandonment timestamp
  indexed), and a ready-made context. Fold the table into an existing context by applying the
  configuration in `OnModelCreating` instead of using the bundled context.
- `EntityFrameworkCoreDeadLetterSink<TContext>.GetHeldAsync` / `CountAsync`: a read-back path for
  inspection and triage, returning the held deliveries newest abandonment first (with an optional
  cap). These are additive queries on the concrete store, not additions to the `IDeadLetterSink`
  interface.
- `AddOrionRelayEntityFrameworkCoreDeadLetterSink(...)`: registers a context factory and the durable
  sink as the application's `IDeadLetterSink`, replacing the no-op default that `AddOrionRelay` adds
  with `TryAdd`, regardless of call order. The concrete store is also resolvable so an operator can
  reach the inspection queries.

### Tests

- Added integration coverage over real file-based SQLite (genuine relational constraints,
  transactions, and primary-key enforcement, not EF InMemory): an abandoned delivery is persisted;
  it is readable through a fresh context after pools are cleared (a simulated restart); re-routing
  the same terminal delivery lands it once with last-write-wins, including under concurrent racing
  writers; the inspection query returns held deliveries newest first and honours the limit; the full
  delivery context (endpoint, payload, headers, attempts, status, abandonment time) round-trips; a
  transport failure is captured as the final error; a delivery without an `EventId` is held under a
  surrogate key so each abandonment is retained; and the row is visible through the context's own
  `DbSet`. Registration tests confirm the durable sink replaces the no-op default in either call
  order and that the interface and concrete registrations resolve to the same instance.

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

[0.4.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.4.0
[0.3.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.3.0
[0.2.2]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.2
[0.2.1]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.1
[0.2.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.1.0
