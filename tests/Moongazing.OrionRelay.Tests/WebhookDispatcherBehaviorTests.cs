namespace Moongazing.OrionRelay.Tests;

using System.Net;
using System.Text;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;
using Moongazing.OrionRelay.Signing;

using Xunit;

/// <summary>
/// Behavioural coverage for <see cref="WebhookDispatcher"/> beyond the smoke tests in
/// <see cref="WebhookDispatcherTests"/>: retry classification per status code, observable backoff
/// timing through the injected delay, request shaping (headers, content type, signing), terminal
/// transport-fault results, and cancellation while backing off. The injected clock, delay and
/// jitter keep every test deterministic and off the wall clock.
/// </summary>
public sealed class WebhookDispatcherBehaviorTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static WebhookMessage Message(string? eventId = "evt_1", string? eventType = "ping") => new()
    {
        Endpoint = new Uri("https://example.test/hook"),
        Body = Encoding.UTF8.GetBytes("{\"event\":\"ping\"}"),
        EventId = eventId,
        EventType = eventType,
    };

    /// <summary>
    /// Builds a dispatcher with a no-op (but recorded) delay so backoff is observable without
    /// sleeping. <paramref name="jitter"/> defaults to 0 (the equal-jitter floor) for predictable
    /// timing; pass 1.0 to see the full computed delay.
    /// </summary>
    private static (WebhookDispatcher Dispatcher, WebhookDiagnostics Diagnostics, List<TimeSpan> Delays) Build(
        HttpMessageHandler handler,
        WebhookDeliveryOptions? options = null,
        IWebhookSigner? signer = null,
        IWebhookDeliveryObserver? observer = null,
        double jitter = 0.0,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var delays = new List<TimeSpan>();
        var diagnostics = new WebhookDiagnostics();
        var dispatcher = new WebhookDispatcher(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            options ?? new WebhookDeliveryOptions(),
            diagnostics,
            signer,
            observer,
            delay: delay ?? ((d, _) =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            }),
            jitter: () => jitter,
            now: () => FixedNow);
        return (dispatcher, diagnostics, delays);
    }

    // ----- retry classification -----------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]       // 408
    [InlineData(HttpStatusCode.TooManyRequests)]      // 429
    [InlineData(HttpStatusCode.InternalServerError)]  // 500
    [InlineData(HttpStatusCode.BadGateway)]           // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]   // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]       // 504
    public async Task Retryable_statuses_are_retried_then_succeed(HttpStatusCode retryable)
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(retryable),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]            // 400
    [InlineData(HttpStatusCode.Unauthorized)]          // 401
    [InlineData(HttpStatusCode.Forbidden)]             // 403
    [InlineData(HttpStatusCode.NotFound)]              // 404
    [InlineData(HttpStatusCode.Conflict)]              // 409
    [InlineData(HttpStatusCode.UnprocessableEntity)]   // 422
    public async Task Non_retryable_4xx_fails_fast_without_a_second_attempt(HttpStatusCode fatal)
    {
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(fatal),
            StubHttpMessageHandler.Status(HttpStatusCode.OK)); // would succeed if it ever retried
        var (dispatcher, diagnostics, delays) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal((int)fatal, result.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(delays); // fatal status means no backoff at all
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]          // 200
    [InlineData(HttpStatusCode.Created)]     // 201
    [InlineData(HttpStatusCode.Accepted)]    // 202
    [InlineData(HttpStatusCode.NoContent)]   // 204
    public async Task Any_2xx_is_a_success(HttpStatusCode success)
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(success));
        var (dispatcher, diagnostics, _) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal((int)success, result.StatusCode);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task A_3xx_is_treated_as_fatal_and_not_retried()
    {
        // 3xx is neither 2xx success nor a retryable class, so the dispatcher abandons it.
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.Moved));
        var (dispatcher, diagnostics, delays) = Build(handler);
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(301, result.StatusCode);
        Assert.Empty(delays);
    }

    // ----- attempt budget -----------------------------------------------------------------

    [Fact]
    public async Task MaxAttempts_of_one_never_retries_even_on_a_retryable_status()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable));
        var (dispatcher, diagnostics, delays) = Build(handler, new WebhookDeliveryOptions { MaxAttempts = 1 });
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.Attempts);
        Assert.Single(handler.Requests);
        Assert.Empty(delays); // budget exhausted before any backoff
    }

    [Fact]
    public async Task Exhausting_the_budget_backs_off_exactly_attempts_minus_one_times()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var (dispatcher, diagnostics, delays) = Build(handler, new WebhookDeliveryOptions { MaxAttempts = 4 });
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(4, result.Attempts);
        // No delay after the final (4th) attempt: 3 backoffs for 4 attempts.
        Assert.Equal(3, delays.Count);
    }

    // ----- backoff shape ------------------------------------------------------------------

    [Fact]
    public async Task Backoff_is_exponential_with_the_equal_jitter_floor_when_jitter_is_zero()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var options = new WebhookDeliveryOptions
        {
            MaxAttempts = 5,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
        };
        var (dispatcher, diagnostics, delays) = Build(handler, options, jitter: 0.0);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        // capped = base * 2^(attempt-1); floor with jitter=0 is capped/2.
        // attempt 1 -> 1000ms/2 = 500ms, 2 -> 2000/2 = 1000, 3 -> 4000/2 = 2000, 4 -> 8000/2 = 4000.
        Assert.Equal(4, delays.Count);
        Assert.Equal(500, delays[0].TotalMilliseconds, 3);
        Assert.Equal(1000, delays[1].TotalMilliseconds, 3);
        Assert.Equal(2000, delays[2].TotalMilliseconds, 3);
        Assert.Equal(4000, delays[3].TotalMilliseconds, 3);
    }

    [Fact]
    public async Task Backoff_adds_the_full_jitter_band_when_jitter_is_one()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var options = new WebhookDeliveryOptions
        {
            MaxAttempts = 3,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
        };
        var (dispatcher, diagnostics, delays) = Build(handler, options, jitter: 1.0);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        // half + jitter*half with jitter=1 equals the full capped value.
        // attempt 1 -> 1000ms, attempt 2 -> 2000ms.
        Assert.Equal(2, delays.Count);
        Assert.Equal(1000, delays[0].TotalMilliseconds, 3);
        Assert.Equal(2000, delays[1].TotalMilliseconds, 3);
    }

    [Fact]
    public async Task Backoff_is_clamped_to_max_delay()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError));
        var options = new WebhookDeliveryOptions
        {
            MaxAttempts = 6,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(3),
        };
        var (dispatcher, diagnostics, delays) = Build(handler, options, jitter: 1.0);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        // Raw exponential would be 1,2,4,8,16s; clamped at 3s the later delays cannot exceed it.
        Assert.All(delays, d => Assert.True(d.TotalMilliseconds <= 3000 + 1e-6, $"delay {d} exceeded MaxDelay"));
        Assert.Equal(3000, delays[^1].TotalMilliseconds, 3);
    }

    // ----- transport faults ---------------------------------------------------------------

    [Fact]
    public async Task A_persistent_transport_fault_exhausts_with_a_null_status_and_the_final_exception()
    {
        var boom = new HttpRequestException("connection reset");
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Throw(boom));
        var (dispatcher, diagnostics, _) = Build(handler, new WebhookDeliveryOptions { MaxAttempts = 3 });
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Attempts);
        Assert.Null(result.StatusCode);
        Assert.Same(boom, result.FinalException);
    }

    [Fact]
    public async Task A_per_attempt_timeout_is_retryable()
    {
        // First attempt hangs past the per-attempt RequestTimeout; the dispatcher's internal
        // linked-token timeout cancels it and treats it as retryable, then the second succeeds.
        var calls = 0;
        var handler = new DelegatingStub(async (request, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // wait for the timeout token
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Use a real (tiny) delay function so the test does not busy-spin; the timeout itself is
        // driven by RequestTimeout, not the wall clock backoff.
        var (dispatcher, diagnostics, _) = Build(
            handler,
            new WebhookDeliveryOptions
            {
                MaxAttempts = 2,
                RequestTimeout = TimeSpan.FromMilliseconds(50),
            });
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
    }

    // ----- request shaping ----------------------------------------------------------------

    [Fact]
    public async Task The_request_is_a_post_to_the_endpoint_with_the_body_and_content_type()
    {
        // The dispatcher disposes the request (and its content) after each attempt, so the body has
        // to be read inside the handler, before disposal, rather than from the recorded request.
        HttpMethod? method = null;
        Uri? uri = null;
        string? contentType = null;
        byte[]? sentBody = null;
        var handler = new DelegatingStub(async (request, ct) =>
        {
            method = request.Method;
            uri = request.RequestUri;
            contentType = request.Content!.Headers.ContentType!.MediaType;
            sentBody = await request.Content!.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (dispatcher, diagnostics, _) = Build(handler);
        using var _ = diagnostics;

        var message = new WebhookMessage
        {
            Endpoint = new Uri("https://example.test/hook"),
            Body = Encoding.UTF8.GetBytes("hello-bytes"),
            ContentType = "application/cloudevents+json",
        };

        await dispatcher.DispatchAsync(message);

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(new Uri("https://example.test/hook"), uri);
        Assert.Equal("application/cloudevents+json", contentType);
        Assert.Equal(Encoding.UTF8.GetBytes("hello-bytes"), sentBody);
    }

    [Fact]
    public async Task Event_id_and_type_headers_are_emitted_when_present()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message(eventId: "evt_42", eventType: "order.created"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("evt_42", request.Headers.GetValues("Orion-Event-Id").Single());
        Assert.Equal("order.created", request.Headers.GetValues("Orion-Event-Type").Single());
    }

    [Fact]
    public async Task Event_id_and_type_headers_are_omitted_when_null()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message(eventId: null, eventType: null));

        var request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.Contains("Orion-Event-Id"));
        Assert.False(request.Headers.Contains("Orion-Event-Type"));
    }

    [Fact]
    public async Task No_signature_header_is_emitted_without_a_signer()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler, signer: null);
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        var request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.Contains("Orion-Signature"));
    }

    [Fact]
    public async Task The_signature_uses_the_injected_clock_timestamp()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler, signer: new WebhookSigner("secret"));
        using var _ = diagnostics;

        await dispatcher.DispatchAsync(Message());

        var request = Assert.Single(handler.Requests);
        var sig = request.Headers.GetValues("Orion-Signature").Single();
        Assert.StartsWith("t=1700000000,v1=", sig, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_retry_re_signs_with_a_fresh_request()
    {
        // Every attempt builds a brand new HttpRequestMessage; a stale/disposed request from a
        // prior attempt would throw. Proves the body is re-buffered per attempt.
        var handler = new StubHttpMessageHandler(
            StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable),
            StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler, signer: new WebhookSigner("secret"));
        using var _ = diagnostics;

        var result = await dispatcher.DispatchAsync(Message());

        Assert.True(result.Succeeded);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
            Assert.StartsWith("t=1700000000,v1=", r.Headers.GetValues("Orion-Signature").Single(), StringComparison.Ordinal));
    }

    // ----- cancellation -------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_during_backoff_propagates_as_an_operation_cancelled()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable));
        using var cts = new CancellationTokenSource();

        // The delay is where the dispatcher observes the token between attempts: cancel there.
        var (dispatcher, diagnostics, _) = Build(
            handler,
            new WebhookDeliveryOptions { MaxAttempts = 4 },
            delay: (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        using var _ = diagnostics;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(Message(), cts.Token));

        // Only the first attempt ran before cancellation aborted the loop.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_null_message_is_rejected()
    {
        var handler = new StubHttpMessageHandler(StubHttpMessageHandler.Status(HttpStatusCode.OK));
        var (dispatcher, diagnostics, _) = Build(handler);
        using var _ = diagnostics;

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync(null!));
    }

    /// <summary>A handler that runs an async delegate, honouring the cancellation token.</summary>
    private sealed class DelegatingStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public DelegatingStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            this.handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
