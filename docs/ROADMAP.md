# OrionRelay Roadmap

Where OrionRelay might go next. This is a list of **ideas under consideration, not commitments**.
Version milestones below are targets, not promises; items can move, merge, slip, or be dropped. The
shipped surface is described in [FEATURES.md](FEATURES.md), and real changes are recorded in
[CHANGELOG.md](../CHANGELOG.md).

OrionRelay is at `0.2.2`: it signs an outbound webhook, sends it over a dedicated `HttpClient`,
retries transient failures with equal-jitter backoff, routes terminal failures to a pluggable
dead-letter sink, and exposes per-attempt metrics. The near-term goal is to close the gaps a real
delivery pipeline hits, durable parking and receiver-side verification first, then settle the public
surface before stabilising it. If something below matters to you, open an issue and say so. Demand
from real workloads is what moves an idea up the list.

---

## Guiding principles

- **Stay focused.** OrionRelay delivers outbound webhooks. It is not a message bus, a scheduler, or
  a queue. Adjacent concerns belong in adjacent libraries.
- **Honest defaults.** Behaviour that is easy to reason about beats behaviour that is clever.
- **Observable by default.** Anything that affects delivery should be visible through telemetry or
  the observer hook.
- **No surprise breakage.** Once the API stabilises, changes follow semantic versioning strictly.

---

## Released / Recently shipped

These items were on this roadmap and have since shipped. They are listed here so the forward plan
below stays free of done work; the authoritative per-release detail is in
[CHANGELOG.md](../CHANGELOG.md).

- **Pluggable dead-letter sink** (`0.2.0`). `IDeadLetterSink` receives every delivery that
  terminates without success, exactly once, carrying the original message, the terminal result, and
  the abandonment timestamp. The default is a no-op (`NullDeadLetterSink`); a bounded
  `InMemoryDeadLetterSink` with oldest-first eviction ships as an opt-in. Sink faults are swallowed
  so a sink outage never turns an already-failed delivery into a thrown exception.
- **HttpClient delivery sender** (`0.2.0`). `AddOrionRelay` wires a dedicated named `HttpClient`,
  left uncapped so the dispatcher's per-attempt timeout governs each attempt.
- **Self-deriving diagnostics meter version** (`0.2.1`). The metrics meter version now derives from
  the package version instead of a hardcoded literal, so telemetry no longer drifts behind a stale
  string.
- **Allocation-free HMAC signing** (`0.2.2`). The signing preimage is assembled into a pooled buffer
  and the signature hex is written directly in lowercase, cutting per-delivery signing allocations to
  a constant 184 B. The wire format is byte-for-byte identical.

---

## Forward plan

Grouped by the next few `0.x` milestones. Ordering reflects what a delivery pipeline needs first,
not difficulty.

### 0.3.0 - Durable parking and receiver-side verification (target Q3 2026)

The two gaps a production deployment hits first. The dead-letter sink is pluggable today, but the
only sink that ships loses its entries on restart, and a receiver still has to hand-roll the HMAC
check shown in the README.

- **A shipped receiver-side verifier.** A small, allocation-conscious verification helper that
  recomputes the MAC over the raw body, enforces a freshness window, and compares in constant time,
  so receivers stop copying the illustrative snippet from the README. Same `t=...,v1=...` contract,
  no new scheme.
- **A durable dead-letter store reference.** A documented `IDeadLetterSink` implementation over
  durable storage (the obvious first target being a relational table) so abandoned deliveries
  survive a restart. The sink contract already carries everything a store needs; this fills the
  in-memory-only gap without widening the interface.

### 0.4.0 - Replay and per-endpoint resilience (target Q4 2026)

Once failures are parked durably, the next questions are getting them back out and not hammering an
endpoint that is already down.

- **Replay-from-dead-letter tooling.** A helper that reads parked `DeadLetterEntry` items from a
  sink and re-dispatches them through the existing pipeline, with the controls a replay needs
  (filtering by event type or age, a dry run, and a cap on replay rate). Replay re-signs with a
  fresh timestamp so the receiver's freshness window still holds.
- **Circuit breaking per endpoint.** Stop sending to an endpoint that is consistently failing and
  let it recover, instead of spending the full attempt budget against a known-dead host on every
  message. Opt-in, observable through the existing telemetry.
- **`Retry-After` awareness.** Honour a `Retry-After` header on `429`/`503` responses when computing
  the next backoff delay, rather than ignoring the receiver's own stated cool-off.

### 0.5.0 - Delivery telemetry and dispatch ergonomics (target Q1 2027)

Deeper visibility and the dispatch-shape conveniences that repeatedly come up, weighed against
keeping the surface small.

- **Distributed tracing.** An `ActivitySource` for delivery spans alongside the existing metrics, so
  an attempt can be correlated across a trace, not just counted.
- **Richer telemetry tags.** Optional endpoint or status-class tags on the existing instruments,
  weighed against cardinality cost.
- **Pluggable retry classification.** Let callers decide whether a given status or exception is
  retryable, instead of the fixed 408/429/5xx-plus-transport rule.
- **Per-message option overrides.** Allow a single `WebhookMessage` to override attempt budget or
  timeout without standing up a separate dispatcher.
- **Concurrency limiting.** An opt-in cap on in-flight deliveries per dispatcher.

### Later / unscheduled

Real but not yet tied to a milestone; demand will decide whether and when they land.

- **Key rotation.** Support more than one active signing secret so secrets rotate without a delivery
  gap, emitting multiple `v1` signatures during the overlap.
- **Signature scheme versioning.** Room for a future `v2` scheme alongside `v1`.
- **Batch dispatch.** A helper for fanning one event out to many subscribers with shared throttling.
- **Subscription management.** Holding the set of endpoints an event fans out to. Likely a separate
  Orion library rather than scope creep here; noted so the boundary stays deliberate.
- **Trimming and NativeAOT validation.** Confirm and document the library's behaviour under trimming
  and AOT.
- **API stabilisation.** Settle the public surface and move off the `0.x` line once the above shake
  out.

---

## Out of scope

- Inbound webhook receiving, routing, or an HTTP endpoint framework. A receiver-side *verification
  helper* (planned for 0.3.0) is in scope; a receiving framework is not.
- A built-in queue or broker. OrionRelay delivers; a durable dead-letter store is a sink behind the
  existing interface, not a message bus baked into the dispatcher.
- Provider-specific signature formats. The `t=...,v1=...` scheme is the contract.

---

Have an idea or a need that is not here? Open an issue. This document changes as that feedback
arrives.
