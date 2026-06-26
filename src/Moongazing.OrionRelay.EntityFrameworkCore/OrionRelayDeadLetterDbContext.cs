namespace Moongazing.OrionRelay.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// A ready-made <see cref="DbContext"/> holding only the OrionRelay dead-letter table, for
/// applications that keep the durable sink in its own context and database. Applications that would
/// rather fold the table into an existing context can skip this type and apply
/// <see cref="DeadLetterRecordConfiguration"/> from their own context instead, then point the sink at
/// that context.
/// </summary>
public class OrionRelayDeadLetterDbContext : DbContext
{
    /// <summary>Create the context with externally supplied options (provider, connection, ...).</summary>
    /// <param name="options">The options that select the provider and connection.</param>
    public OrionRelayDeadLetterDbContext(DbContextOptions<OrionRelayDeadLetterDbContext> options)
        : base(options)
    {
    }

    /// <summary>The persisted dead-letter records.</summary>
    public DbSet<DeadLetterRecord> DeadLetters => Set<DeadLetterRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new DeadLetterRecordConfiguration());
    }
}
