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
        // terminal delivery idempotent (the second write resolves to the existing row by key). It is
        // the one column that must stay bounded, because a key has to be index-friendly on every
        // provider (SQL Server, for instance, will not key an unbounded string). 1024 is a generous
        // cap that holds any realistic EventId and the 32-character surrogate, while staying inside
        // the relational key-size limits. The original EventId is also preserved uncapped in its own
        // column below, so nothing the message carried is lost even when it backs the key.
        builder.HasKey(e => e.DeliveryId);
        builder.Property(e => e.DeliveryId).HasMaxLength(1024);

        // The remaining columns hold free-form values the sink copies verbatim from the abandoned
        // delivery (the endpoint URL, the payload bytes, the content type, and the two header values).
        // None of these is a key, and any of them can legitimately exceed a fixed cap (a long signed
        // URL, a content type with parameters, a custom event type or id), so they are left unbounded
        // (nvarchar(max) / text). A configured cap shorter than the source would silently truncate on
        // some providers and throw on others; an abandoned delivery must be stored exactly as received.
        builder.Property(e => e.Endpoint).IsRequired();
        builder.Property(e => e.Body).IsRequired();
        builder.Property(e => e.ContentType).IsRequired();
        builder.Property(e => e.EventType);
        builder.Property(e => e.EventId);
        builder.Property(e => e.Attempts).IsRequired();
        builder.Property(e => e.DeadLetteredAtTicks).IsRequired();

        // Serves the newest-first ordering and the age filter the inspection query uses.
        builder.HasIndex(e => e.DeadLetteredAtTicks);
    }
}
