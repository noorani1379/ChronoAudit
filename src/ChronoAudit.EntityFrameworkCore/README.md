# ChronoAudit.EntityFrameworkCore

The audit engine of [ChronoAudit](https://github.com/noorani1379/ChronoAudit) for Entity Framework Core 8 and 10. You add it to the application that owns the database; it does not bring a database provider (use `Microsoft.EntityFrameworkCore.SqlServer` or `Npgsql.EntityFrameworkCore.PostgreSQL`).

## What is in it

- `AuditableDbContext`, `AuditableEntityBase`, `IAuditableEntity`: every `SaveChanges` writes a field-level audit row in the same transaction; deletes become soft deletes.
- The tamper-evident chain: HMAC-SHA256 hash workers (`AddHashWorkers`), anchors outside the database (`AddAnchorWorker`), key rotation (`IChainMaintenance`), and migration helpers for the guard triggers (`CreateChronoAuditAuditGuard`, `CreateChronoAuditStoreGuards`).
- Reading: `IChangeReader` (change history of a table or record), `IAuditSearch` (by actor, resource, action), `IExtractReader`.
- The store (`ModelBuilder.ApplyChronoAuditStore`): the API activity log and mutation intents, saved reports (`ISavedReportService`), export jobs (`IExportService`, `IExportProducer`), the business timeline (`ITimelineWriter`, `ITimelineReader`, `IEvidenceService`), analytics (`IAnalyticsService`, `AddAnalyticsSubject`).
- Personal data: `IPersonalDataRevealPolicy`, `[SensitiveData]`.

The ASP.NET Core pieces (middleware, endpoints) are in `ChronoAudit.AspNetCore`.

## Example

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
        modelBuilder.ApplyChronoAuditStore(this);
    }
}

public static class Startup
{
    public static void Register(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(configuration.GetConnectionString("Default")));

        services.AddChronoAudit(configuration, environment)   // reads ChronoAudit:HmacKey (32+ bytes) and the ChronoAudit:* sections
            .AddModule<AppDbContext>("app")
            .UseStore<AppDbContext>()
            .AddHashWorkers();

        // Without ASP.NET Core, supply the actor and the correlation id yourself (IAuditActor, ICorrelationContext, scoped).
    }
}
```

Documentation: [github.com/noorani1379/ChronoAudit](https://github.com/noorani1379/ChronoAudit).
