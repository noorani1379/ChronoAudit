# ChronoAudit

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8.0 & 10.0](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-purple.svg)](https://dotnet.microsoft.com/)

An audit trail for **.NET 8 and .NET 10** applications that use **Entity Framework Core** and **ASP.NET Core**. It records every change you save, makes the records tamper-evident, logs every API call, and lets people read, search, analyze and export the history over HTTP or from code.

It is a library, not a service: you add the packages, one migration and a few lines of startup code. Tested on **SQL Server** and **PostgreSQL** (SQLite for tests and demos).

## What you get

- **Automatic change capture.** Every `SaveChanges` writes a field-level audit row (who, when, old and new values) in the same transaction as the change. Deleting an entity becomes a soft delete.
- **Tamper evidence.** Audit rows are chained with HMAC-SHA256 by a background worker; database triggers refuse updates and deletes of audit rows; signed *anchor* files outside the database reveal a removed tail; the key can be rotated without breaking old rows.
- **API activity log.** One row per API call (user, endpoint, status, time, correlation id) with a masked copy of the body of write calls, plus a **mutation intent** recorded before each write so you can see requests that arrived and never finished.
- **History and search.** Read the change history of a table or a record, search the audit trail by actor, resource and action, with keyset paging and an access scope (All, Group, Own).
- **Analytics.** Group and count audit rows, API calls, timeline events or your own tables by up to three dimensions in one SQL statement, with period comparison.
- **Saved reports and exports.** Store analytics queries, share them with a group, and export results, audit rows, the activity log or the timeline as CSV or XLSX by a resumable background worker that is safe on several nodes.
- **Business timeline and evidence.** Record business events idempotently, read them by subject, case or person, watch data lag and consistency, and issue an evidence bundle for a record.
- **Personal data protection.** Mask secrets at the source and personal fields on read; reveal only through your own policy, and audit every reveal.
- **Ready-made HTTP API.** `MapChronoAudit("policy")` maps all of the above behind your authorization policy.

## Install

```bash
dotnet add package ChronoAudit.EntityFrameworkCore   # audit capture, chain, store, analytics, exports, timeline
dotnet add package ChronoAudit.AspNetCore            # middleware, filters and HTTP endpoints
```

You also reference your own EF Core database provider (`Microsoft.EntityFrameworkCore.SqlServer` or `Npgsql.EntityFrameworkCore.PostgreSQL`).

## Quick start

**1. An auditable context** (the base class stamps `CreatedBy`, `ModifiedBy`, `DeletedBy`, their times and `IsDeleted`):

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class Book : AuditableEntityBase
{
    public int Id { get; set; }

    public required string Title { get; set; }
}

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options, IAuditActor actor, ICorrelationContext correlation,
    IAuditLogAccessor logs, AuditSignalChannel signal, TimeProvider? clock = null, IChronoAuditCalendar? calendar = null)
    : AuditableDbContext(options, actor, correlation, logs, signal, clock, calendar)
{
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyChronoAuditStore(this);   // the activity log, reports, exports and timeline tables
    }
}
```

**2. Startup** (`Program.cs`). `ChronoAudit__HmacKey` (32+ bytes) comes from the environment; in Development a public key is used when none is set.

```csharp program
using ChronoAudit.AspNetCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddAuthentication();   // plus your scheme: JWT bearer, cookies, ...
builder.Services.AddAuthorizationBuilder().AddPolicy("chronoaudit", p => p.RequireAuthenticatedUser());

builder.Services.AddChronoAudit(builder.Configuration, builder.Environment)
    .AddModule<AppDbContext>("app")
    .UseStore<AppDbContext>()
    .AddActivityLog()
    .AddHashWorkers()
    .AddAnchorWorker()
    .AddBuiltInAnalytics()
    .AddSavedReports()
    .AddExports()
    .AddExportWorker()
    .AddTimeline()
    .AddDataLag();

builder.Services.AddChronoAuditAspNetCore(builder.Configuration);
builder.Services.AddChronoAuditMutationIntents();

var app = builder.Build();

app.UseChronoAuditActivityLog();   // before authentication, so refused calls are logged too
app.UseAuthentication();
app.UseAuthorization();
app.MapChronoAudit("chronoaudit");
app.Run();
```

**3. One migration.** Create it with `dotnet ef migrations add Initial`, and add the guard triggers that make the audit tables append-only:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

public partial class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ... generated tables ...
        migrationBuilder.CreateChronoAuditAuditGuard();                  // the audit table of your context
        migrationBuilder.CreateChronoAuditStoreGuards("chronoaudit");    // the schema of the store tables
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropChronoAuditStoreGuards("chronoaudit");
        migrationBuilder.DropChronoAuditAuditGuard();
        // ... generated drops ...
    }
}
```

That is all: save a `Book` and `GET /chronoaudit/history/app/tables/Books/changes` (with the All scope) shows what changed. The full walk-through is in [Getting started](docs/guide/getting-started.md), and a complete runnable host is in [`samples/ChronoAudit.Sample.WebApi`](samples/ChronoAudit.Sample.WebApi).

## Documentation

| Guide | About |
|---|---|
| [Getting started](docs/guide/getting-started.md) | install, context, startup, key, migration |
| [Configuration reference](docs/guide/configuration.md) | every option, its default and where it is read |
| [Audit capture and history](docs/guide/audit-and-history.md) | what is captured, reading and searching history |
| [Hash chain, key rotation, anchors](docs/guide/hash-chain-and-anchors.md) | tamper evidence in depth |
| [Activity log and mutation intents](docs/guide/activity-log.md) | middleware, masking, intents |
| [Analytics](docs/guide/analytics.md) | subjects, queries, filters, your own subject |
| [Saved reports](docs/guide/saved-reports.md) | sharing, concurrency, running |
| [Exports](docs/guide/exports.md) | producers, formats, workers, your own producer |
| [Timeline, data lag, consistency, evidence](docs/guide/timeline.md) | business events and proof |
| [HTTP endpoints](docs/guide/http-endpoints.md) | every route with real examples |
| [Personal data and secrets](docs/guide/personal-data.md) | masking and the reveal policy |
| [Security model](docs/guide/security.md) | threats, defenses, limits, database permissions |
| [Operations](docs/guide/operations.md) | workers, metrics, alerts, backups, runbook |
| [Error codes](docs/guide/error-codes.md) | every stable code |
| [Performance](docs/guide/performance.md) | design and measured numbers |

## Supported

- **.NET:** `net8.0` and `net10.0` (EF Core 8 and 10).
- **Databases:** tested on SQL Server 2025 (Express, in Docker) and PostgreSQL 17, plus SQLite in tests. Older versions that EF Core 8 or 10 support are expected to work but are not in the test matrix.
- **Status:** `0.1.0-preview.1`. The public API may still change before 1.0.

## License

[MIT](LICENSE).
