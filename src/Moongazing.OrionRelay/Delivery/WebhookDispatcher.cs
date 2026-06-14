namespace Moongazing.OrionRelay.Delivery;

using System.Net;
using System.Net.Http.Headers;

using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;
using Moongazing.OrionRelay.Signing;

/// <summary>
/// Default <see cref="IWebhookDispatcher"/>. Signs each attempt, retries transient failures
/// (transport faults, request timeouts, HTTP 408/429/5xx) with equal-jitter exponential backoff,
/// and stops on success, a non-retryable HTTP status, or an exhausted attempt budget.
/// </summary>
public sealed class WebhookDispatcher : IWebhookDispatcher
{
    private readonly HttpClient httpClient;
    private readonly WebhookDeliveryOptions options;
    private readonly WebhookDiagnostics diagnostics;
    private readonly IWebhookSigner? signer;
    private readonly IWebhookDeliveryObserver observer;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<double> jitter;
    private readonly Func<DateTimeOffset> now;

    /// <summary>Create a dispatcher.</summary>
    /// <param name="httpClient">The client used for every attempt.</param>
    /// <param name="options">Delivery tuning. Validated on construction.</param>
    /// <param name="diagnostics">The shared metrics instance.</param>
    /// <param name="signer">The request signer, or null to send unsigned.</param>
    /// <param name="observer">The delivery observer, or null for none.</param>
    public WebhookDispatcher(
        HttpClient httpClient,
        WebhookDeliveryOptions options,
        WebhookDiagnostics diagnostics,
        IWebhookSigner? signer = null,
        IWebhookDeliveryObserver? observer = null)
        : this(httpClient, options, diagnostics, signer, observer,
               delay: Task.Delay, jitter: Random.Shared.NextDouble, now: () => DateTimeOffset.UtcNow)
    {
    }

    internal WebhookDispatcher(
        HttpClient httpClient,
        WebhookDeliveryOptions options,
        WebhookDiagnostics diagnostics,
        IWebhookSigner? signer,
        IWebhookDeliveryObserver? observer,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<double> jitter,
        Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentNullException.ThrowIfNull(now);
        options.Validate();

        this.httpClient = httpClient;
        this.options = options;
        this.diagnostics = diagnostics;
        this.signer = signer;
        this.observer = observer ?? NullWebhookDeliveryObserver.Instance;
        this.delay = delay;
        this.jitter = jitter;
        this.now = now;
    }

    /// <inheritdoc />
    public async Task<WebhookDeliveryResult> DispatchAsync(
        WebhookMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var eventTypeTag = new KeyValuePair<string, object?>("event_type", message.EventType ?? "(none)");
        int? lastStatus = null;
        Exception? lastException = null;
        var attemptsMade = 0;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            attemptsMade = attempt;
            cancellationToken.ThrowIfCancellationRequested();

            AttemptOutcome outcome;
            try
            {
                outcome = await SendOnceAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-driven cancellation is not a delivery failure; it aborts the dispatch.
                throw;
            }

            lastStatus = outcome.StatusCode;
            lastException = outcome.Exception;
            NotifyAttempt(message, attempt, outcome);

            diagnostics.Attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome.Kind switch
            {
                AttemptKind.Success => "success",
                AttemptKind.Retryable => "retryable",
                _ => "fatal",
            }));

            if (outcome.Kind == AttemptKind.Success)
            {
                RecordCompletion(message, attempt, succeeded: true, eventTypeTag);
                return WebhookDeliveryResult.Success(attempt, outcome.StatusCode!.Value);
            }

            if (outcome.Kind == AttemptKind.Fatal || attempt == options.MaxAttempts)
            {
                break;
            }

            await delay(NextBackoff(attempt), cancellationToken).ConfigureAwait(false);
        }

        var failure = WebhookDeliveryResult.Failure(attemptsMade, lastStatus, lastException);
        RecordCompletion(message, attemptsMade, succeeded: false, eventTypeTag);
        SafeObserve(() => observer.OnExhausted(message, failure));
        return failure;
    }

    private async Task<AttemptOutcome> SendOnceAsync(WebhookMessage message, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.RequestTimeout);

        using var request = BuildRequest(message);
        try
        {
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            return IsSuccess(response.StatusCode)
                ? new AttemptOutcome(AttemptKind.Success, status, null)
                : new AttemptOutcome(IsRetryableStatus(response.StatusCode) ? AttemptKind.Retryable : AttemptKind.Fatal, status, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // The per-attempt timeout fired (not the caller's token): a retryable timeout.
            return new AttemptOutcome(AttemptKind.Retryable, null, ex);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptOutcome(AttemptKind.Retryable, null, ex);
        }
    }

    private HttpRequestMessage BuildRequest(WebhookMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, message.Endpoint);
        var content = new ByteArrayContent(message.Body.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(message.ContentType);
        request.Content = content;

        if (signer is not null)
        {
            var timestamp = now();
            request.Headers.TryAddWithoutValidation(
                options.SignatureHeader, signer.Sign(message.Body.Span, timestamp));
        }

        if (message.EventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Orion-Event-Id", message.EventId);
        }
        if (message.EventType is not null)
        {
            request.Headers.TryAddWithoutValidation("Orion-Event-Type", message.EventType);
        }

        return request;
    }

    private TimeSpan NextBackoff(int attempt)
    {
        // Exponential, clamped, with equal jitter: keep half the computed delay as a floor and
        // randomise the other half so a fleet of senders does not retry in lockstep.
        var exponent = attempt - 1;
        var scaled = options.BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(scaled, options.MaxDelay.TotalMilliseconds);
        var half = capped / 2;
        var withJitter = half + (jitter() * half);
        return TimeSpan.FromMilliseconds(withJitter);
    }

    private void RecordCompletion(WebhookMessage message, int attempts, bool succeeded, KeyValuePair<string, object?> eventTypeTag)
    {
        diagnostics.Delivered.Add(1,
            new KeyValuePair<string, object?>("outcome", succeeded ? "succeeded" : "failed"),
            eventTypeTag);
        diagnostics.AttemptsPerDelivery.Record(attempts, eventTypeTag);
    }

    private void NotifyAttempt(WebhookMessage message, int attempt, AttemptOutcome outcome) =>
        SafeObserve(() => observer.OnAttempt(message, attempt, outcome.StatusCode, outcome.Exception));

    private static void SafeObserve(Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // observer is observability, not load-bearing
        catch (Exception)
#pragma warning restore CA1031
        {
            // An observer fault must never break delivery.
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.TooManyRequests
        || (int)status >= 500;

    private enum AttemptKind
    {
        Success,
        Retryable,
        Fatal,
    }

    private readonly record struct AttemptOutcome(AttemptKind Kind, int? StatusCode, Exception? Exception);
}
