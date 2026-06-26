namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionRelay;
using Moongazing.OrionRelay.Delivery;
using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// Registration tests for the EF Core dead-letter sink wiring. They use SQLite only as an arbitrary
/// real provider so the context factory resolves; the persistence behaviour is covered by the SQLite
/// integration tests.
/// </summary>
public sealed class RegistrationTests
{
    [Fact]
    public void AddOrionRelayEntityFrameworkCoreDeadLetterSink_replaces_the_default_sink()
    {
        var services = new ServiceCollection();

        // AddOrionRelay adds the no-op sink with TryAdd; the durable registration uses a plain Add and
        // must take precedence regardless of call order, so resolving IDeadLetterSink yields the EF
        // store rather than the no-op.
        services.AddOrionRelay(signingSecret: "secret");
        services.AddOrionRelayEntityFrameworkCoreDeadLetterSink(o => o.UseSqlite("Data Source=:memory:"));

        using var provider = services.BuildServiceProvider();

        var sink = provider.GetRequiredService<IDeadLetterSink>();
        Assert.IsType<EntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext>>(sink);
    }

    [Fact]
    public void AddOrionRelayEntityFrameworkCoreDeadLetterSink_takes_precedence_when_called_before_AddOrionRelay()
    {
        var services = new ServiceCollection();

        services.AddOrionRelayEntityFrameworkCoreDeadLetterSink(o => o.UseSqlite("Data Source=:memory:"));
        services.AddOrionRelay(signingSecret: "secret");

        using var provider = services.BuildServiceProvider();

        var sink = provider.GetRequiredService<IDeadLetterSink>();
        Assert.IsType<EntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext>>(sink);
    }

    [Fact]
    public void the_concrete_store_is_resolvable_for_inspection_queries()
    {
        var services = new ServiceCollection();
        services.AddOrionRelayEntityFrameworkCoreDeadLetterSink(o => o.UseSqlite("Data Source=:memory:"));

        using var provider = services.BuildServiceProvider();

        // The same singleton is returned whether resolved as the interface or the concrete store, so
        // an operator can reach GetHeldAsync without a second instance over a different context.
        var asInterface = provider.GetRequiredService<IDeadLetterSink>();
        var asConcrete = provider.GetRequiredService<EntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext>>();
        Assert.Same(asInterface, asConcrete);
    }
}
