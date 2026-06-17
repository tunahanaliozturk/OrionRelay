# OrionRelay Roadmap

Where OrionRelay might go next. This is a list of **ideas under consideration, not commitments**.
There are no dates and no guarantees; items can move, merge, or be dropped. The shipped surface is
described in [FEATURES.md](FEATURES.md), and real changes are recorded in
[CHANGELOG.md](../CHANGELOG.md).

OrionRelay is at `0.1.0`. The near-term goal is to harden the existing send path and keep the public
API small and honest before stabilising it. If something below matters to you, open an issue and say
so. Demand from real workloads is what moves an idea up the list.

---

## Guiding principles

- **Stay focused.** OrionRelay delivers outbound webhooks. It is not a message bus, a scheduler, or
  a queue. Adjacent concerns belong in adjacent libraries.
- **Honest defaults.** Behaviour that is easy to reason about beats behaviour that is clever.
- **Observable by default.** Anything that affects delivery should be visible through telemetry or
  the observer hook.
- **No surprise breakage.** Once the API stabilises, changes follow semantic versioning strictly.

---

## Ideas under consideration

### Delivery

- **Pluggable retry classification.** Let callers decide whether a given status or exception is
  retryable, instead of the fixed 408/429/5xx-plus-transport rule.
- **`Retry-After` awareness.** Honour a `Retry-After` header on 429/503 responses when computing the
  next backoff delay.
- **Per-message option overrides.** Allow a single `WebhookMessage` to override attempt budget or
  timeout without a separate dispatcher.
- **Batch dispatch.** A helper for fanning one event out to many subscribers with shared throttling.

### Signing and verification

- **A shipped receiver-side verifier.** A small, allocation-conscious verification helper so
  receivers do not hand-roll the HMAC comparison shown in the README.
- **Key rotation.** Support more than one active signing secret so secrets can be rotated without a
  delivery gap, emitting multiple `v1` signatures during the overlap.
- **Signature scheme versioning.** Room for a future `v2` scheme alongside `v1`.

### Resilience and back pressure

- **Concurrency limiting.** An opt-in cap on in-flight deliveries per dispatcher.
- **Circuit breaking per endpoint.** Stop hammering an endpoint that is consistently failing and let
  it recover.
- **Persistent redelivery contract.** A documented pattern (not necessarily a storage
  implementation) for parking exhausted deliveries and replaying them later, building on the
  observer hook.

### Observability

- **Distributed tracing.** An `ActivitySource` for delivery spans alongside the existing metrics,
  so an attempt can be correlated across a trace.
- **Richer telemetry tags.** Optional endpoint or status-class tags, weighed against cardinality
  cost.

### Packaging and reach

- **Trimming and NativeAOT validation.** Confirm and document the library's behaviour under
  trimming and AOT.
- **API stabilisation.** Settle the public surface and move off the `0.x` line once the above
  shake out.

---

## Out of scope

- Inbound webhook receiving, routing, or an HTTP endpoint framework.
- A durable store or queue. OrionRelay delivers; persistence is the caller's choice.
- Provider-specific signature formats. The `t=...,v1=...` scheme is the contract.

---

Have an idea or a need that is not here? Open an issue. This document changes as that feedback
arrives.
</content>
