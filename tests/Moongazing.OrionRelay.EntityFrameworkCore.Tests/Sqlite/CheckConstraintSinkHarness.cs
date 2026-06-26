namespace Moongazing.OrionRelay.EntityFrameworkCore.Tests.Sqlite;

using Microsoft.EntityFrameworkCore;

using Moongazing.OrionRelay.EntityFrameworkCore;

/// <summary>
/// A file-backed SQLite harness whose schema carries a CHECK constraint (via
/// <see cref="CheckConstraintDbContext"/>), so a write can be made to fail with a genuine,
/// non-duplicate <see cref="DbUpdateException"/>. Used by the tests that assert a non-duplicate save
/// failure surfaces rather than being reconciled as an idempotent re-route.
/// </summary>
internal sealed class CheckConstraintSinkHarness : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly Factory factory;

    private CheckConstraintSinkHarness(string databasePath, Factory factory)
    {
        this.databasePath = databasePath;
        this.factory = factory;
    }

    public EntityFrameworkCoreDeadLetterSink<CheckConstraintDbContext> CreateSink() => new(factory);

    public static async Task<CheckConstraintSinkHarness> CreateAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"orionrelay-efcore-chk-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=30";

        var options = new DbContextOptionsBuilder<CheckConstraintDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var factory = new Factory(options);

        await using (var context = await factory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        return new CheckConstraintSinkHarness(databasePath, factory);
    }

    public ValueTask DisposeAsync()
    {
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
            // Best effort; the per-test temp file is reclaimed with the temp directory.
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Factory : IDbContextFactory<CheckConstraintDbContext>
    {
        private readonly DbContextOptions<CheckConstraintDbContext> options;

        public Factory(DbContextOptions<CheckConstraintDbContext> options) => this.options = options;

        public CheckConstraintDbContext CreateDbContext() => new(options);

        public Task<CheckConstraintDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
