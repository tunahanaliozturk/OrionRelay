namespace Moongazing.OrionRelay.Tests;

using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionRelay;
using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.Signing;

using Xunit;

public sealed class OrionRelayRegistrationTests
{
    [Fact]
    public void AddOrionRelay_resolves_a_dispatcher()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetService<IWebhookDispatcher>();

        Assert.NotNull(dispatcher);
    }

    [Fact]
    public void AddOrionRelay_with_a_secret_registers_a_signer()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IWebhookSigner>());
    }

    [Fact]
    public void AddOrionRelay_without_a_secret_registers_no_signer()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay();

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IWebhookSigner>());
    }

    [Fact]
    public void AddOrionRelay_honours_configured_options()
    {
        var services = new ServiceCollection();
        services.AddOrionRelay("secret", o => o.MaxAttempts = 7);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(7, provider.GetRequiredService<WebhookDeliveryOptions>().MaxAttempts);
    }

    [Fact]
    public void AddOrionRelay_rejects_invalid_options_eagerly()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddOrionRelay("secret", o => o.MaxAttempts = 0));
    }

    [Fact]
    public void AddOrionRelay_registers_a_no_op_dead_letter_sink_by_default()
    {
        // The default sink must retain nothing: a prolonged receiver outage cannot be allowed to
        // grow the process working set by holding every abandoned delivery (bodies included) for
        // the process lifetime. In-memory retention is opt-in, not the default.
        var services = new ServiceCollection();
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();
        Assert.Same(NullDeadLetterSink.Instance, provider.GetService<IDeadLetterSink>());
    }

    [Fact]
    public void AddOrionRelay_honours_a_consumer_registered_dead_letter_sink()
    {
        var services = new ServiceCollection();
        var custom = new CustomDeadLetterSink();
        services.AddSingleton<IDeadLetterSink>(custom);
        services.AddOrionRelay("secret");

        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetService<IDeadLetterSink>());
    }

    private sealed class CustomDeadLetterSink : IDeadLetterSink
    {
        public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
