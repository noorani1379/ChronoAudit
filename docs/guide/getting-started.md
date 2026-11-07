# Getting started

This guide takes an ASP.NET Core application from nothing to a working audit trail: every `SaveChanges` is recorded, the records are chained with an HMAC, every API call is logged, and the history can be read back over HTTP. Every code block here is compiled against the real packages; the same flow runs in [`samples/ChronoAudit.Sample.WebApi`](../../samples/ChronoAudit.Sample.WebApi).

## 1. What you need

- .NET 8 or .NET 10, Entity Framework Core 8 or 10 (the package follows the target framework of your application).
- SQL Server or PostgreSQL. ChronoAudit does not ship a database provider: your application references `Microsoft.EntityFrameworkCore.SqlServer` or `Npgsql.EntityFrameworkCore.PostgreSQL` itself. SQLite works for tests and demos only; its guard triggers are weaker and decimal aggregates are limited on EF Core 8.
- A database user that may create tables (for the migration) and create triggers.

## 2. Install

```bash
dotnet add package ChronoAudit.EntityFrameworkCore
dotnet add package ChronoAudit.AspNetCore
```

`ChronoAudit.Core` comes with them. Use only `ChronoAudit.EntityFrameworkCore` when you have no web application (a worker service, a console tool); then you register the actor and the correlation context yourself (see section 7).

## 3. Make your entities and DbContext auditable

An entity that derives from `AuditableEntityBase` (or implements `IAuditableEntity`) gets ten extra columns that the context fills for you: `CreatedBy`, `CreatedByName`, `CreatedAtUtc`, `ModifiedBy`, `ModifiedByName`, `ModifiedAtUtc`, `DeletedBy`, `DeletedByName`, `DeletedAtUtc` and `IsDeleted`. Removing such an entity becomes a soft delete, and a global query filter hides deleted rows.

Your context derives from `AuditableDbContext`. Its constructor receives services that `AddChronoAudit` and `AddChronoAuditAspNetCore` register, so a plain pass-through constructor is all you write. `ApplyChronoAuditStore` adds the tables of the activity log, saved reports, exports and the timeline to the same context (you can also keep them in a second context, see `UseStore` in the [operations guide](operations.md)).

```csharp
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public sealed class Book : AuditableEntityBase
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public required string Author { get; set; }
}

public sealed class LibraryDbContext(
    DbContextOptions<LibraryDbContext> options,
    IAuditActor actor,
    ICorrelationContext correlation,
    IAuditLogAccessor auditLogAccessor,
    AuditSignalChannel signal,
    TimeProvider? clock = null,
    IChronoAuditCalendar? calendar = null)
    : AuditableDbContext(options, actor, correlation, auditLogAccessor, signal, clock, calendar)
{
    public DbSet<Book> Books => Set<Book>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("library");
        modelBuilder.ApplyChronoAuditStore(this, new ChronoAuditStoreOptions { Schema = "chronoaudit" });
    }
}
```

Every table of your model that you want audited must belong to an entity with these columns. A table whose entity does not implement `IAuditableEntity` is still audited on insert, update and delete (the audit row keeps its last state); it is just not soft deleted. Mark an entity with `IExcludedFromAutoAudit` to leave it out, and a property with `[SensitiveData]` to store `***MASKED***` instead of its value (see the [personal data guide](personal-data.md)).

## 4. Register ChronoAudit and map the endpoints

```csharp program
using ChronoAudit.AspNetCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LibraryDb"), sql =>
        sql.MigrationsHistoryTable("__EFMigrationsHistory", "library")));

// Your own authentication (JWT bearer, cookies, ...): ChronoAudit only needs an authenticated user and a policy.
builder.Services.AddAuthentication();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("chronoaudit", policy => policy.RequireAuthenticatedUser());

// AddChronoAudit reads ChronoAudit:HmacKey and the other ChronoAudit:* sections of the configuration.
builder.Services.AddChronoAudit(builder.Configuration, builder.Environment)
    .AddModule<LibraryDbContext>("library")
    .UseStore<LibraryDbContext>()
    .AddActivityLog()
    .AddHashWorkers()
    .AddAnchorWorker()
    .AddBuiltInAnalytics()
    .AddSavedReports()
    .AddExports()
    .AddExportWorker()
    .AddTimeline()
    .AddDataLag()
    .AddConsistencyWorker();

// The HTTP actor (from the user's claims), the correlation id (from X-Correlation-Id) and the access scope.
builder.Services.AddChronoAuditAspNetCore(builder.Configuration);
builder.Services.AddChronoAuditMutationIntents();

var app = builder.Build();

// First, so a call that authentication or authorization refuses (401, 403) is still recorded.
app.UseChronoAuditActivityLog();

app.UseAuthentication();
app.UseAuthorization();

app.MapChronoAudit("chronoaudit");

app.Run();
```

What each call does:

| Call | Effect |
|---|---|
| `AddChronoAudit(configuration, environment)` | Reads the HMAC key and the `ChronoAudit:*` options. Registers the audit services. A missing or weak key stops the host outside Development and Testing. |
| `AddModule<T>("library")` | Audits one `AuditableDbContext`. The name (1 to 64 letters, digits, `-`, `_`) appears in URLs and anchors; pick it once and keep it. Call it once per audited database. |
| `UseStore<T>()` | Says which context holds the store tables (activity log, reports, exports, timeline). |
| `AddActivityLog()` | The API activity log (needs `UseStore`). |
| `AddHashWorkers()` | Background services that put pending rows into the HMAC chain. |
| `AddAnchorWorker()` | Writes signed anchors (head of each chain) to a directory, to detect a removed tail. |
| `AddBuiltInAnalytics()` / `AddSavedReports()` / `AddExports()` / `AddExportWorker()` | Analytics, saved reports, export jobs and the worker that runs them. |
| `AddTimeline()` / `AddDataLag()` / `AddConsistencyWorker()` | Business timeline; the `X-Data-Lag-Seconds` header on read calls; the worker that runs your consistency checks ([timeline](timeline.md)). |
| `AddChronoAuditAspNetCore()` | Actor, correlation and access scope from the HTTP request. |
| `AddChronoAuditMutationIntents()` | Records the intent (who, what, SHA-256 of the body) of every POST, PUT, PATCH and DELETE before it runs. |
| `UseChronoAuditActivityLog()` | The middleware that writes one activity row per API call. Put it before `UseAuthentication`, so refused calls (401, 403) are recorded too; it reads the user after the call has run. |
| `MapChronoAudit("chronoaudit")` | Maps every route group under `/chronoaudit`, all behind the policy you name. A blank policy name throws: the endpoints are never mapped open. |

Add only what you use. For example, a host that only needs the audit trail calls `AddChronoAudit(...).AddModule<T>("library").AddHashWorkers()`.

## 5. Configuration

`appsettings.json` carries the non-secret settings. The key is a secret: set it in the environment, never in a file you commit.

```json
{
  "ConnectionStrings": {
    "LibraryDb": "Server=localhost;Database=Library;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "ChronoAudit": {
    "PersonalData": {
      "NumberFields": [ "PhoneNumber" ],
      "TextFields": [ "FullName", "Email" ]
    },
    "Anchors": { "Directory": "App_Data/chronoaudit-anchors" },
    "Exports": { "Directory": "App_Data/chronoaudit-exports" }
  }
}
```

```bash
# a random secret of at least 32 bytes; keep a safe copy, you need the same key to verify old rows
export ChronoAudit__HmacKey="$(openssl rand -base64 48)"
```

On Windows PowerShell: `$env:ChronoAudit__HmacKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))`.

The key is the UTF-8 text of the value. In `Development` and `Testing`, a built-in public key is used when none is set, so `dotnet run` works on a laptop; every other environment refuses to start without a real key. Changing the key later makes older rows fail verification; rotate with `ChronoAudit:PreviousHmacKeys` (see [hash chain and anchors](hash-chain-and-anchors.md)).

Every option, with its default, is in the [configuration reference](configuration.md). All options can be set in `appsettings.json` (`ChronoAudit:<Section>:<Name>`), in code with the `configure` callback of `AddChronoAudit`, or through environment variables (`ChronoAudit__Exports__RetentionDays`).

## 6. Create the database objects

Create the migration with the EF Core tools. `dotnet ef` starts your host in the `Development` environment, so the development key is allowed.

```bash
dotnet ef migrations add Initial --context LibraryDbContext
```

The guard triggers that make the audit tables append-only are SQL, not model, so EF cannot generate them. Add them to the migration yourself:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

public partial class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ... generated table definitions ...

        migrationBuilder.CreateChronoAuditAuditGuard("library");
        migrationBuilder.CreateChronoAuditStoreGuards("chronoaudit");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropChronoAuditStoreGuards("chronoaudit");
        migrationBuilder.DropChronoAuditAuditGuard("library");

        // ... generated drops ...
    }
}
```

Use the schema names of your model (`library` for the audit table, `chronoaudit` for the store). Then apply it: `dotnet ef database update --context LibraryDbContext`. Without the guards everything still works, but nothing at the database level stops someone from editing or deleting audit rows. The triggers do not guard `TRUNCATE` or DDL; the anchors detect a removed tail.

## 7. Hosts without ASP.NET Core

Register the three services the HTTP package normally provides:

```csharp
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class JobActor : IAuditActor
{
    public string? UserId => "nightly-import";

    public string? DisplayName => "Nightly import";
}

public sealed class JobCorrelation : ICorrelationContext
{
    public Guid CorrelationId { get; } = Guid.NewGuid();
}

public static class JobRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddScoped<IAuditActor, JobActor>();
        services.AddScoped<ICorrelationContext, JobCorrelation>();
    }
}
```

`IAuditActor.UserId` is stamped on every audit row; `null` is written as the system user (`ChronoAudit:Audit:SystemUserId`, default `System`).

## 8. Try it

Run the application, then:

```bash
curl -X POST https://localhost:5001/books -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"title":"Dune","author":"Frank Herbert"}'
curl "https://localhost:5001/chronoaudit/history/library/tables/Books/changes" -H "Authorization: Bearer <token>"
```

Next: [audit and history](audit-and-history.md), [HTTP endpoints](http-endpoints.md), [activity log](activity-log.md).
