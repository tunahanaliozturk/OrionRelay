<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionRelay are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.0]: https://github.com/tunahanaliozturk/OrionRelay/releases/tag/v0.1.0
