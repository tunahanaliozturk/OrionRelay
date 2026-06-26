namespace Moongazing.OrionRelay.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionRelay.Delivery;

/// <summary>
/// Registration helpers that wire the EF Core durable dead-letter sink into the OrionRelay services.
/// </summary>
public static class OrionRelayEntityFrameworkCoreServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OrionRelayDeadLetterDbContext"/> through an
    /// <see cref="IDbContextFactory{TContext}"/> and use an
    /// <see cref="EntityFrameworkCoreDeadLetterSink{TContext}"/> over it as the application's
    /// <see cref="IDeadLetterSink"/>. Call this alongside <c>AddOrionRelay(...)</c>; because that
    /// method only adds the no-op sink when none is registered (via <c>TryAdd</c>), this registration
    /// takes precedence regardless of call order, so abandoned deliveries are parked durably instead
    /// of discarded.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureDbContext">
    /// Configures the context's provider and connection (for example
    /// <c>o =&gt; o.UseNpgsql(connectionString)</c>). The caller chooses the provider; this package
    /// references only EF Core's relational surface.
    /// </param>
    public static IServiceCollection AddOrionRelayEntityFrameworkCoreDeadLetterSink(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext) =>
        services.AddOrionRelayEntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext>(configureDbContext);

    /// <summary>
    /// Register <typeparamref name="TContext"/> through an <see cref="IDbContextFactory{TContext}"/>
    /// and use an <see cref="EntityFrameworkCoreDeadLetterSink{TContext}"/> over it as the
    /// application's <see cref="IDeadLetterSink"/>. Use this overload when the dead-letter record is
    /// mapped into your own context (which must apply <see cref="DeadLetterRecordConfiguration"/>)
    /// rather than the bundled <see cref="OrionRelayDeadLetterDbContext"/>.
    /// </summary>
    /// <typeparam name="TContext">The context type that maps <see cref="DeadLetterRecord"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configureDbContext">Configures the context's provider and connection.</param>
    public static IServiceCollection AddOrionRelayEntityFrameworkCoreDeadLetterSink<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        services.AddDbContextFactory<TContext>(configureDbContext);

        // Use a plain registration (not TryAdd): calling this method is an explicit choice to park
        // abandoned deliveries durably, so it replaces the no-op default that AddOrionRelay adds with
        // TryAdd. Registered as a concrete type as well so an operator can resolve the store and call
        // its inspection queries, which are not on the IDeadLetterSink interface.
        services.AddSingleton<EntityFrameworkCoreDeadLetterSink<TContext>>(sp =>
            new EntityFrameworkCoreDeadLetterSink<TContext>(
                sp.GetRequiredService<IDbContextFactory<TContext>>()));

        services.AddSingleton<IDeadLetterSink>(sp =>
            sp.GetRequiredService<EntityFrameworkCoreDeadLetterSink<TContext>>());

        return services;
    }
}
