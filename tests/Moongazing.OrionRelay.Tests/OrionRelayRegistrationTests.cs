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
}
