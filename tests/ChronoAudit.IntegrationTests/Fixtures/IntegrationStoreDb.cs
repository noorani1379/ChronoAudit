using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.IntegrationTests;

public sealed class IntegrationStoreDb(DbContextOptions<IntegrationStoreDb> options) : DbContext(options)
{
    public DbSet<ApiRequestLog> ApiRequestLogs => Set<ApiRequestLog>();

    public DbSet<ApiRequestBody> ApiRequestBodies => Set<ApiRequestBody>();

    public DbSet<ApiMutationIntent> ApiMutationIntents => Set<ApiMutationIntent>();

    public DbSet<SavedReport> SavedReports => Set<SavedReport>();

    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    public DbSet<TimelineEvent> TimelineEvents => Set<TimelineEvent>();

    public DbSet<TimelineParticipant> TimelineParticipants => Set<TimelineParticipant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyChronoAuditStore(this, new ChronoAuditStoreOptions { Schema = "chronoaudit" });
    }
}

