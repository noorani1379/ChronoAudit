using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Where the ChronoAudit store tables live (section <c>ChronoAudit:Store</c>).</summary>
public sealed partial class ChronoAuditStoreOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Store";

    /// <summary>The database schema of the store tables: 1 to 64 letters, digits or underscores. Default <c>chronoaudit</c>. SQLite has no schemas and ignores it.</summary>
    public string Schema { get; set; } = "chronoaudit";

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">The schema name is not valid.</exception>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Schema) || !SchemaName().IsMatch(Schema))
        {
            throw new InvalidOperationException("ChronoAudit:Store:Schema must be 1 to 64 letters, digits or underscores.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaName();
}

/// <summary>Adds the ChronoAudit store tables to the model of a host context.</summary>
public static class ChronoAuditModelBuilderExtensions
{
    /// <summary>
    /// Adds the store tables to the model: <c>ApiRequestLogs</c>, <c>ApiRequestBodies</c>, <c>ApiMutationIntents</c>, <c>SavedReports</c>, <c>ExportJobs</c>, <c>TimelineEvents</c> and <c>TimelineParticipants</c>. Call it from <c>OnModelCreating</c> of the store context and
    /// create the tables with the host's own migration; call <c>CreateChronoAuditStoreGuards</c> in that migration for the guard triggers.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="context">The context being built (its provider name chooses the quoting of filtered indexes).</param>
    /// <param name="options">The store options; null means the defaults.</param>
    /// <returns>The model builder.</returns>
    public static ModelBuilder ApplyChronoAuditStore(this ModelBuilder modelBuilder, DbContext context, ChronoAuditStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new ChronoAuditStoreOptions();
        options.Validate();

        var dialect = ChronoAuditDialect.For(context);
        var schema = dialect.Kind == ProviderKind.Sqlite ? null : options.Schema;

        modelBuilder.Entity<ApiRequestLog>(b =>
        {
            b.ToTable("ApiRequestLogs", schema, t => t.HasTrigger("TR_ApiRequestLogs_Guard"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.HttpMethod).HasMaxLength(ApiRequestLogEntry.MaxMethodLength).IsRequired();
            b.Property(x => x.Path).HasMaxLength(ApiRequestLogEntry.MaxPathLength).IsRequired();
            b.Property(x => x.UserId).HasMaxLength(ApiRequestLogEntry.MaxIdLength);
            b.Property(x => x.ActAsUserId).HasMaxLength(ApiRequestLogEntry.MaxIdLength);
            b.Property(x => x.SessionId).HasMaxLength(ApiRequestLogEntry.MaxIdLength);
            b.Property(x => x.EndpointKey).HasMaxLength(ApiRequestLogEntry.MaxEndpointKeyLength);
            b.Property(x => x.QueryString).HasMaxLength(ApiRequestLogEntry.MaxQueryStringLength);
            b.Property(x => x.ErrorCode).HasMaxLength(ApiRequestLogEntry.MaxErrorCodeLength);
            b.Property(x => x.IpAddress).HasMaxLength(ApiRequestLogEntry.MaxIpAddressLength);
            b.Property(x => x.UserAgent).HasMaxLength(ApiRequestLogEntry.MaxUserAgentLength);
            b.Property(x => x.PreviousHash).HasMaxLength(64);
            b.Property(x => x.PayloadHash).HasMaxLength(64);

            b.HasIndex(x => x.RequestId).IsUnique().HasDatabaseName("UX_ApiRequestLogs_RequestId");
            b.HasIndex(x => x.ChainIndex).IsUnique().HasFilter(dialect.IsNotNull("ChainIndex")).HasDatabaseName("UX_ApiRequestLogs_ChainIndex");
            b.HasIndex(x => x.OccurredAtUtc).HasDatabaseName("IX_ApiRequestLogs_OccurredAtUtc");
            b.HasIndex(x => new { x.UserId, x.OccurredAtUtc }).HasDatabaseName("IX_ApiRequestLogs_User");
            b.HasIndex(x => new { x.EndpointKey, x.OccurredAtUtc }).HasDatabaseName("IX_ApiRequestLogs_Endpoint");
            b.HasIndex(x => new { x.SessionId, x.OccurredAtUtc }).HasDatabaseName("IX_ApiRequestLogs_Session");
            b.HasIndex(x => x.CorrelationId).HasFilter(dialect.IsNotNull("CorrelationId")).HasDatabaseName("IX_ApiRequestLogs_Correlation");

            // The hash worker looks for rows that are not hashed yet: only those are in this index, so an idle look is one cheap seek.
            b.HasIndex(x => x.Id).HasFilter(dialect.IsNull("PayloadHash")).HasDatabaseName("IX_ApiRequestLogs_Unhashed");
        });

        modelBuilder.Entity<ApiRequestBody>(b =>
        {
            b.ToTable("ApiRequestBodies", schema, t => t.HasTrigger("TR_ApiRequestBodies_Guard"));
            b.HasKey(x => x.ApiRequestLogId);
            b.Property(x => x.ApiRequestLogId).ValueGeneratedNever();
            b.Property(x => x.ContentType).HasMaxLength(ApiRequestLogEntry.MaxContentTypeLength);

            // The body is evidence of its log row: deleting the row must be refused, not cascade.
            b.HasOne<ApiRequestLog>().WithOne(x => x.Body).HasForeignKey<ApiRequestBody>(x => x.ApiRequestLogId).OnDelete(DeleteBehavior.NoAction);

            // Body and FileMetadata are unbounded strings; the 64 KB cap is enforced when the body is built.
        });

        modelBuilder.Entity<ApiMutationIntent>(b =>
        {
            b.ToTable("ApiMutationIntents", schema, t => t.HasTrigger("TR_ApiMutationIntents_Guard"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.UserId).HasMaxLength(ApiRequestLogEntry.MaxIdLength);
            b.Property(x => x.ActAsUserId).HasMaxLength(ApiRequestLogEntry.MaxIdLength);
            b.Property(x => x.SessionId).HasMaxLength(ApiRequestLogEntry.MaxIdLength);
            b.Property(x => x.Method).HasMaxLength(ApiRequestLogEntry.MaxMethodLength).IsRequired();
            b.Property(x => x.Path).HasMaxLength(ApiRequestLogEntry.MaxPathLength).IsRequired();
            b.Property(x => x.EndpointKey).HasMaxLength(ApiRequestLogEntry.MaxEndpointKeyLength);
            b.Property(x => x.BodySha256).HasMaxLength(64).IsRequired();
            b.Property(x => x.PreviousHash).HasMaxLength(64);
            b.Property(x => x.PayloadHash).HasMaxLength(64);

            // One intent per request: a retried write stores one row. The result row carries the same RequestId.
            b.HasIndex(x => x.RequestId).IsUnique().HasDatabaseName("UX_ApiMutationIntents_RequestId");
            b.HasIndex(x => x.ChainIndex).IsUnique().HasFilter(dialect.IsNotNull("ChainIndex")).HasDatabaseName("UX_ApiMutationIntents_ChainIndex");
            b.HasIndex(x => x.StartedAtUtc).HasDatabaseName("IX_ApiMutationIntents_StartedAtUtc");
            b.HasIndex(x => new { x.UserId, x.StartedAtUtc }).HasDatabaseName("IX_ApiMutationIntents_User");
            b.HasIndex(x => x.Id).HasFilter(dialect.IsNull("PayloadHash")).HasDatabaseName("IX_ApiMutationIntents_Unhashed");
        });

        modelBuilder.Entity<SavedReport>(b =>
        {
            b.ToTable("SavedReports", schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.OwnerUserId).HasMaxLength(SavedReport.MaxIdLength).IsRequired();
            b.Property(x => x.Title).HasMaxLength(SavedReport.MaxTitleLength).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(SavedReport.MaxIdLength).IsRequired();
            b.Property(x => x.DefinitionJson).IsRequired();
            b.Property(x => x.SharedWithGroupId).HasMaxLength(SavedReport.MaxIdLength);

            // Portable optimistic concurrency (PLAN D7): renewed by every change, no provider-specific row version.
            b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            b.Ignore(x => x.IsShared);

            b.HasIndex(x => new { x.OwnerUserId, x.UpdatedAtUtc }).HasDatabaseName("IX_SavedReports_Owner");
            b.HasIndex(x => new { x.SharedWithGroupId, x.UpdatedAtUtc }).HasFilter(dialect.IsNotNull("SharedWithGroupId")).HasDatabaseName("IX_SavedReports_SharedGroup");
        });

        modelBuilder.Entity<TimelineEvent>(b =>
        {
            b.ToTable("TimelineEvents", schema, t => t.HasTrigger("TR_TimelineEvents_Guard"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedOnAdd();
            b.Property(x => x.EventType).HasMaxLength(128).IsRequired();
            b.Property(x => x.SubjectType).HasMaxLength(128).IsRequired();
            b.Property(x => x.SubjectId).HasMaxLength(128).IsRequired();
            b.Property(x => x.GroupKey).HasMaxLength(128);
            b.Property(x => x.ActorUserId).HasMaxLength(64);
            b.Property(x => x.ActorDisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.ActorRole).HasMaxLength(128);
            b.Property(x => x.OnBehalfOfUserId).HasMaxLength(64);
            b.Property(x => x.AccessGroupId).HasMaxLength(64);
            b.Property(x => x.Payload).IsRequired();

            b.HasIndex(x => x.SourceEventId).IsUnique().HasDatabaseName("UX_TimelineEvents_SourceEventId");
            b.HasIndex(x => new { x.SubjectType, x.SubjectId, x.OccurredAtUtc, x.Id }).HasDatabaseName("IX_TimelineEvents_Subject");
            b.HasIndex(x => new { x.GroupKey, x.OccurredAtUtc, x.Id }).HasFilter(dialect.IsNotNull("GroupKey")).HasDatabaseName("IX_TimelineEvents_Group");
            b.HasIndex(x => new { x.ActorUserId, x.OccurredAtUtc }).HasDatabaseName("IX_TimelineEvents_Actor");
            b.HasIndex(x => new { x.OnBehalfOfUserId, x.OccurredAtUtc }).HasFilter(dialect.IsNotNull("OnBehalfOfUserId")).HasDatabaseName("IX_TimelineEvents_OnBehalfOf");
            b.HasIndex(x => new { x.AccessGroupId, x.OccurredAtUtc }).HasFilter(dialect.IsNotNull("AccessGroupId")).HasDatabaseName("IX_TimelineEvents_AccessGroup");
            b.HasIndex(x => new { x.EventType, x.OccurredAtUtc }).HasDatabaseName("IX_TimelineEvents_Type");
            b.HasIndex(x => x.CorrelationId).HasFilter(dialect.IsNotNull("CorrelationId")).HasDatabaseName("IX_TimelineEvents_Correlation");
        });

        modelBuilder.Entity<TimelineParticipant>(b =>
        {
            b.ToTable("TimelineParticipants", schema);
            b.HasKey(x => new { x.GroupKey, x.UserId, x.Role });
            b.Property(x => x.GroupKey).HasMaxLength(128);
            b.Property(x => x.UserId).HasMaxLength(64);
            b.Property(x => x.Role).HasMaxLength(64);
            b.HasIndex(x => new { x.UserId, x.GroupKey }).HasDatabaseName("IX_TimelineParticipants_User");
        });

        modelBuilder.Entity<ExportJob>(b =>
        {
            b.ToTable("ExportJobs", schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.RequestedByUserId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Producer).HasMaxLength(64).IsRequired();
            b.Property(x => x.ParametersJson).IsRequired();
            b.Property(x => x.ScopeGroupId).HasMaxLength(64);
            b.Property(x => x.CheckpointCursor).HasMaxLength(1024);
            b.Property(x => x.FileName).HasMaxLength(64);
            b.Property(x => x.Sha256).HasMaxLength(64);
            b.Property(x => x.LeaseOwner).HasMaxLength(128);
            b.Property(x => x.ErrorCode).HasMaxLength(128);
            b.Property(x => x.ErrorMessage).HasMaxLength(ExportJob.MaxErrorLength);
            b.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            b.Ignore(x => x.IsTerminal);

            // The worker looks for the oldest waiting job; a user lists their own newest first.
            b.HasIndex(x => new { x.State, x.CreatedAtUtc }).HasDatabaseName("IX_ExportJobs_State");
            b.HasIndex(x => new { x.RequestedByUserId, x.CreatedAtUtc }).HasDatabaseName("IX_ExportJobs_User");
        });

        return modelBuilder;
    }
}

/// <summary>The context type that holds the store tables (set by <c>UseStore</c>).</summary>
/// <param name="ContextType">The store context type.</param>
internal sealed record ChronoAuditStoreRegistration(Type ContextType);
