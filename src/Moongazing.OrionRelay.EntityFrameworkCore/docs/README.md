# OrionRelay.EntityFrameworkCore

[![NuGet](https://img.shields.io/nuget/v/OrionRelay.EntityFrameworkCore.svg)](https://www.nuget.org/packages/OrionRelay.EntityFrameworkCore/)

A durable Entity Framework Core dead-letter sink for
[OrionRelay](https://www.nuget.org/packages/OrionRelay/). It implements the existing
`IDeadLetterSink` over a relational table, so webhook deliveries that exhaust their attempt budget
survive a process restart and are shared across instances, instead of being held in the
process-local in-memory sink that loses its entries on restart.

Part of the **Orion** family.

## What it does

- **Implements `IDeadLetterSink`** over EF Core. The interface is not widened: this is a durable
  reference implementation behind the existing seam, the half of the dead-letter story the in-memory
  sink left open.
- **Persists the whole abandoned delivery.** Each `DeadLetterEntry` the dispatcher routes here is
  stored as a `DeadLetterRecord`: the target endpoint, the payload, the content type and event
  headers a receiver would have seen, the attempt count, the final error, the last HTTP status, and
  the abandonment timestamp.
- **Idempotent on the delivery id.** `DeliveryId` is the primary key, so when the dispatcher
  re-routes a replayed terminal delivery the second write resolves to the existing row rather than
  inserting a duplicate. It is the message's `EventId` when one was set; otherwise a surrogate keys
  the row, since an unidentified delivery has nothing stable to deduplicate on.
- **Exposes a read-back path for triage.** `GetHeldAsync` returns the parked deliveries newest first
  (with an optional cap) and `CountAsync` counts them, for inspection and a later replay. These are
  additive queries on the concrete store, not on the `IDeadLetterSink` interface.
- Provider agnostic: depends only on `Microsoft.EntityFrameworkCore.Relational`, so you choose the
  database provider (SQL Server, PostgreSQL, SQLite, and so on).

## Install

```
dotnet add package OrionRelay.EntityFrameworkCore
```

You also need an EF Core provider package for your database, for example
`Microsoft.EntityFrameworkCore.SqlServer` or `Npgsql.EntityFrameworkCore.PostgreSQL`.

## Quick start

Register the sink **before** `AddOrionRelay()`, configuring the context inline. The bundled
`OrionRelayDeadLetterDbContext` is ready to use:

```csharp
using Moongazing.OrionRelay.EntityFrameworkCore;

// Register the durable sink first: AddOrionRelay only adds the no-op sink if no IDeadLetterSink
// is already present. This also registers a context factory the sink resolves a short-lived
// context from per abandoned delivery.
builder.Services.AddOrionRelayEntityFrameworkCoreDeadLetterSink(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Webhooks")));

builder.Services.AddOrionRelay(signingSecret: "whsec_your_shared_secret");
```

A delivery that exhausts its attempt budget is now parked in the database instead of discarded.

## Inspecting held deliveries

`IDeadLetterSink` is write-only by design. To triage what is parked, resolve the concrete store and
query it:

```csharp
var sink = app.Services.GetRequiredService<EntityFrameworkCoreDeadLetterSink<OrionRelayDeadLetterDbContext>>();

var held = await sink.GetHeldAsync(limit: 100, ct);
foreach (var record in held)
{
    Console.WriteLine($"{record.DeliveryId} -> {record.Endpoint} after {record.Attempts} attempts");
}
```

## Using your own DbContext

If you already have a context, host the dead-letter table in it by applying the configuration in
`OnModelCreating`, then point the sink at that context:

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new DeadLetterRecordConfiguration());
        // ... your own entities
    }
}

builder.Services.AddOrionRelayEntityFrameworkCoreDeadLetterSink<AppDbContext>(o =>
    o.UseSqlServer(connectionString));
```

`DeadLetterRecordConfiguration` also accepts a custom table name if the default
`OrionRelayDeadLetters` clashes with an existing table.

## Migrations

The sink does not create the schema. Add a migration for the mapped entity the usual way and apply
it as part of your deployment:

```
dotnet ef migrations add AddOrionRelayDeadLetters
dotnet ef database update
```

## Versioning

Multi-targets `net8.0`, `net9.0`, and `net10.0`, pinning the matching EF Core major per target
framework. Tracks the OrionRelay version line. See the
[root README](https://github.com/tunahanaliozturk/OrionRelay) and
[CHANGELOG](https://github.com/tunahanaliozturk/OrionRelay/blob/main/CHANGELOG.md).

## License

Licensed under the [MIT License](https://github.com/tunahanaliozturk/OrionRelay/blob/main/LICENSE).
