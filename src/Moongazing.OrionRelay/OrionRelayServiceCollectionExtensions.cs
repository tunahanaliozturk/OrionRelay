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
    /// used; otherwise delivery runs without one.
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
                sp.GetService<IWebhookDeliveryObserver>());
        });

        return services;
    }
}
