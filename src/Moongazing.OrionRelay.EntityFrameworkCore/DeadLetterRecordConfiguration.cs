namespace Moongazing.OrionRelay.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Maps <see cref="DeadLetterRecord"/> for relational providers. Apply it from a host
/// <see cref="DbContext.OnModelCreating"/> (via
/// <c>builder.ApplyConfiguration(new DeadLetterRecordConfiguration())</c>) to fold the dead-letter
/// table into an existing context instead of using <see cref="OrionRelayDeadLetterDbContext"/>. The
/// mapping keys the table by <see cref="DeadLetterRecord.DeliveryId"/>, which supplies the primary
/// key the sink relies on for an idempotent re-route, and indexes the abandonment timestamp so the
/// inspection query can return entries in newest-first order from the index.
/// </summary>
public sealed class DeadLetterRecordConfiguration : IEntityTypeConfiguration<DeadLetterRecord>
{
    /// <summary>The default table name used for the dead-letter records.</summary>
    public const string DefaultTableName = "OrionRelayDeadLetters";

    private readonly string tableName;

    /// <summary>Configure the entity against the default table name.</summary>
    public DeadLetterRecordConfiguration()
        : this(DefaultTableName)
    {
    }

    /// <summary>Configure the entity against a caller-supplied table name.</summary>
    /// <param name="tableName">The table the records are stored in.</param>
    public DeadLetterRecordConfiguration(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        this.tableName = tableName;
    }

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeadLetterRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable(tableName);

        // DeliveryId is the primary key: this is the unique constraint that makes a re-routed
        // terminal delivery idempotent (the second write resolves to the existing row by key). A
        // length cap keeps the column index-friendly on providers that will not index an unbounded
        // string.
        builder.HasKey(e => e.DeliveryId);
        builder.Property(e => e.DeliveryId).HasMaxLength(256);

        builder.Property(e => e.Endpoint).IsRequired();
        builder.Property(e => e.Body).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(256).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(256);
        builder.Property(e => e.EventId).HasMaxLength(256);
        builder.Property(e => e.Attempts).IsRequired();
        builder.Property(e => e.DeadLetteredAtTicks).IsRequired();

        // Serves the newest-first ordering and the age filter the inspection query uses.
        builder.HasIndex(e => e.DeadLetteredAtTicks);
    }
}
