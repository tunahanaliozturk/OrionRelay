namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that wraps a real factory and counts how many
/// contexts it hands out. The sink draws one context per operation and one more only when it walks
/// the duplicate-key reconciliation path, so the count is the lever the reconciliation-scoping tests
/// use to prove that path runs only when an insert genuinely conflicts.
/// </summary>
internal sealed class CountingDbContextFactory : IDbContextFactory<OrionRelayDeadLetterDbContext>
{
    private readonly IDbContextFactory<OrionRelayDeadLetterDbContext> inner;
    private int created;

    public CountingDbContextFactory(IDbContextFactory<OrionRelayDeadLetterDbContext> inner) => this.inner = inner;

    /// <summary>The number of contexts handed out so far.</summary>
    public int Created => Volatile.Read(ref created);

    public OrionRelayDeadLetterDbContext CreateDbContext()
    {
        Interlocked.Increment(ref created);
        return inner.CreateDbContext();
    }

    public async Task<OrionRelayDeadLetterDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref created);
        return await inner.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
    }
}
