namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> that builds a fresh
/// <see cref="OrionRelayDeadLetterDbContext"/> from fixed options on each call. The sink creates one
/// context per operation, so handing out a new instance every time matches how it is used in
/// production and keeps concurrent operations on independent contexts.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<OrionRelayDeadLetterDbContext>
{
    private readonly DbContextOptions<OrionRelayDeadLetterDbContext> options;

    public TestDbContextFactory(DbContextOptions<OrionRelayDeadLetterDbContext> options) => this.options = options;

    public OrionRelayDeadLetterDbContext CreateDbContext() => new(options);

    public Task<OrionRelayDeadLetterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
