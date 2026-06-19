namespace Moongazing.OrionRelay;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;
using Moongazing.OrionRelay.Signing;

/// <summary>
/// Registration helpers for OrionRelay.
/// </summary>
public static class OrionRelayServiceCollectionExtensions
{
    /// <summary>
    /// Register the webhook dispatcher, its dedicated <see cref="HttpClient"/>, the shared
    /// diagnostics, and a signer built from <paramref name="signingSecret"/>. If a consumer
    /// registers an <see cref="IWebhookDeliveryObserver"/> before resolving the dispatcher it is
    /// used; otherwise delivery runs without one. Deliveries that exhaust their attempt budget are
    /// routed to an <see cref="IDeadLetterSink"/>. The default is the no-op
    /// <see cref="NullDeadLetterSink"/>, which retains nothing and so cannot grow the process
    /// working set during a prolonged receiver outage. Register your own sink (for example
    /// <see cref="InMemoryDeadLetterSink"/> for in-process inspection, or a durable store) before
    /// the dispatcher resolves to capture abandoned deliveries instead.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="signingSecret">
    /// The HMAC secret used to sign every request. Pass null or empty to send unsigned (not
    /// recommended outside trusted networks).
    /// </param>
    /// <param name="configure">Optional delivery tuning.</param>
    public static IServiceCollection AddOrionRelay(
        this IServiceCollection services,
        string? signingSecret = null,
        Action<WebhookDeliveryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new WebhookDeliveryOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton<WebhookDiagnostics>();

        // Default to a no-op sink: a safe, bounded-by-construction default that retains nothing.
        // The in-memory sink is bounded but still holds bodies, so it is opt-in, not the default.
        services.TryAddSingleton<IDeadLetterSink>(NullDeadLetterSink.Instance);

        if (!string.IsNullOrEmpty(signingSecret))
        {
            services.TryAddSingleton<IWebhookSigner>(new WebhookSigner(signingSecret));
        }

        services.AddHttpClient(nameof(WebhookDispatcher), client =>
        {
            // The dispatcher enforces its own per-attempt timeout; keep the client uncapped so the
            // two do not race and produce a confusing 'caller cancelled' on a slow endpoint.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.TryAddSingleton<IWebhookDispatcher>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new WebhookDispatcher(
                factory.CreateClient(nameof(WebhookDispatcher)),
                sp.GetRequiredService<WebhookDeliveryOptions>(),
                sp.GetRequiredService<WebhookDiagnostics>(),
                sp.GetService<IWebhookSigner>(),
                sp.GetService<IWebhookDeliveryObserver>(),
                sp.GetService<IDeadLetterSink>());
        });

        return services;
    }
}
