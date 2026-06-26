namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// A test harness backed by a real, file-based SQLite database. A file (not EF's in-memory provider,
/// and not shared-cache memory) is used so the sink runs against genuine relational constraints,
/// transactions, and the actual primary-key enforcement the idempotent re-route depends on, and so
/// that closing every context and reopening the file genuinely simulates a process restart. Each
/// harness owns a unique database file under the temp directory and deletes it on disposal.
/// </summary>
internal sealed class SqliteSinkHarness : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly TestDbContextFactory factory;

    private SqliteSinkHarness(string databasePath, TestDbContextFactory factory)
    {
        this.databasePath = databasePath;
        this.factory = factory;
    }

    /// <summary>The context factory the sink draws a fresh context from per operation.</summary>
    public IDbContextFactory<OrionRelayDeadLetterDbContext> Factory => factory;

    /// <summary>
    /// A sink bound to this harness's database. A new instance is cheap and holds no state, so the
    /// restart tests build a second sink over the same file to read back what an earlier sink wrote.
    /// </summary>
    public EntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext> CreateSink() => new(factory);

    /// <summary>
    /// A sink whose context factory is wrapped to count the contexts it hands out. The reconciliation
    /// tests read <see cref="CountingDbContextFactory.Created"/> to prove the duplicate-key path runs
    /// only when an insert genuinely conflicts: a no-insert update and a first-time insert draw exactly
    /// one context, while a real key collision draws a second for the reconciling re-read.
    /// </summary>
    public (EntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext> Sink, CountingDbContextFactory Counter)
        CreateCountingSink()
    {
        var counter = new CountingDbContextFactory(factory);
        return (new(counter), counter);
    }

    /// <summary>Create the harness and its schema. The returned harness is ready to use.</summary>
    public static async Task<SqliteSinkHarness> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"orionrelay-efcore-{Guid.NewGuid():N}.db");

        // Busy timeout lets a writer wait for a concurrent writer's lock instead of failing fast with
        // SQLITE_BUSY, which keeps the parallel re-route test honest (one row wins the key) rather
        // than flaky. The sink still sees real constraint enforcement; only lock contention is smoothed.
        var connectionString = $"Data Source={databasePath};Default Timeout=30";

        var options = new DbContextOptionsBuilder<OrionRelayDeadLetterDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var factory = new TestDbContextFactory(options);

        await using (var context = await factory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        return new SqliteSinkHarness(databasePath, factory);
    }

    public ValueTask DisposeAsync()
    {
        // Drop any pooled connections to the file before deleting it; SQLite keeps a handle open while
        // a connection is pooled, which would block the delete on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch (IOException)
        {
            // Best effort: a stray handle on a CI agent should not fail an otherwise green test. The
            // temp file is named per test and will be reclaimed with the temp directory.
        }

        return ValueTask.CompletedTask;
    }
}
