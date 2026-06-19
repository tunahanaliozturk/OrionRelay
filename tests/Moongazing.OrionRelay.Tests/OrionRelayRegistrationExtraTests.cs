namespace Moongazing.OrionRelay.Tests;

using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionRelay;
using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Diagnostics;
using Moongazing.OrionRelay.Observers;

using Xunit;

/// <summary>
/// Additional DI wiring coverage for <see cref="OrionRelayServiceCollectionExtensions"/>:
/// lifetimes, observer pickup, idempotent registration, and argument guards.
/// </summary>
public sealed class OrionRelayRegistrationExtraTests
{
    [Fact]
    public void AddOrionRelay_rejects_a_null_service_collection()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OrionRelayServiceCollectionExtensions.AddOrionRelay(null!));
    }

    [Fact]
    public void Diagnostics_and_dispatcher_are_singletons()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<WebhookDiagnostics>(), provider.GetRequiredService<WebhookDiagnostics>());
        Assert.Same(provider.GetRequiredService<IWebhookDispatcher>(), provider.GetRequiredService<IWebhookDispatcher>());
    }

    [Fact]
    public void A_pre_registered_observer_is_picked_up_by_the_dispatcher_factory()
    {
        var observer = new CountingObserver();
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookDeliveryObserver>(observer);
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();

        // The dispatcher resolves without error and the registered observer is the resolvable one.
        Assert.NotNull(provider.GetRequiredService<IWebhookDispatcher>());
        Assert.Same(observer, provider.GetRequiredService<IWebhookDeliveryObserver>());
    }

    [Fact]
    public void An_empty_secret_registers_no_signer()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay(string.Empty);

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<Moongazing.OrionRelay.Signing.IWebhookSigner>());
    }

    [Fact]
    public void Calling_AddOrionRelay_twice_does_not_duplicate_the_options_singleton()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay("secret", o => o.MaxAttempts = 9);
        // TryAdd semantics mean the second call's options are ignored, not stacked.
        services.AddOrionRelay("secret", o => o.MaxAttempts = 2);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetServices<WebhookDeliveryOptions>().ToList();

        Assert.Single(options);
        Assert.Equal(9, options[0].MaxAttempts);
    }

    [Fact]
    public void Negative_base_delay_is_rejected_eagerly_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddOrionRelay("secret", o => o.BaseDelay = TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void The_default_dead_letter_sink_retains_nothing_and_cannot_grow_unbounded()
    {
        // Guards the unbounded-default regression: the default sink must discard, not accumulate.
        var services = new ServiceCollection();
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IDeadLetterSink>();

        Assert.Same(NullDeadLetterSink.Instance, sink);
        Assert.IsNotType<InMemoryDeadLetterSink>(sink);
    }

    [Fact]
    public void A_consumer_can_opt_into_a_bounded_in_memory_sink()
    {
        // Opt-in retention is still available, and it is bounded by construction.
        var services = new ServiceCollection();
        services.AddSingleton<IDeadLetterSink>(new InMemoryDeadLetterSink(capacity: 16));
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();
        var sink = Assert.IsType<InMemoryDeadLetterSink>(provider.GetRequiredService<IDeadLetterSink>());

        Assert.Equal(16, sink.Capacity);
    }

    private sealed class CountingObserver : IWebhookDeliveryObserver
    {
        public void OnAttempt(WebhookMessage message, int attempt, int? statusCode, Exception? exception)
        {
        }

        public void OnExhausted(WebhookMessage message, WebhookDeliveryResult result)
        {
        }
    }
}
