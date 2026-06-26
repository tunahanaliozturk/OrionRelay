namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// A test-only context that maps the same dead-letter table but adds a CHECK constraint the sink
/// never deliberately violates. It exists to manufacture a genuine, non-duplicate
/// <see cref="DbUpdateException"/> on demand (by writing a record whose <c>Attempts</c> is negative),
/// so the tests can prove a non-duplicate save failure is not swallowed as an idempotent re-route.
/// SQLite enforces CHECK constraints at write time, which makes the failure real rather than mocked.
/// </summary>
internal sealed class CheckConstraintDbContext : DbContext
{
    public CheckConstraintDbContext(DbContextOptions<CheckConstraintDbContext> options)
        : base(options)
    {
    }

    public DbSet<DeadLetterRecord> DeadLetters => Set<DeadLetterRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new DeadLetterRecordConfiguration());
        modelBuilder.Entity<DeadLetterRecord>()
            .ToTable(DeadLetterRecordConfiguration.DefaultTableName, t =>
                t.HasCheckConstraint("CK_DeadLetter_Attempts_NonNegative", "\"Attempts\" >= 0"));
    }
}
