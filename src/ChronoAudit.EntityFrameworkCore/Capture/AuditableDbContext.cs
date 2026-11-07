using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// A <see cref="DbContext"/> whose every save is audited. On each <c>SaveChangesAsync</c> it stamps <see cref="IAuditableEntity"/> rows
/// (created/modified/deleted by and at), turns deletes of those rows into soft deletes (with a global query filter that hides them),
/// writes one <see cref="AuditLogEntry"/> per changed row (old and new values, secrets masked) in the same transaction, and rejects
/// mutations of <see cref="IAppendOnly"/> rows and of the audit table itself.
/// </summary>
/// <remarks>
/// Only the asynchronous save exists: the synchronous overload throws, because it would skip the audit pipeline.
/// Derive one context per database (module) and register it with <c>AddDbContext</c>. Hosts extend the pipeline through
/// <see cref="OnBeforeSaveAsync"/>, <see cref="OnBeforeAuditAsync"/>, <see cref="OnAfterSaved"/> and <see cref="IsAuditExcluded"/>.
/// </remarks>
public abstract class AuditableDbContext : DbContext
{
    private const string MaskedValue = "***MASKED***";
    private static readonly ConcurrentDictionary<string, bool> SensitivePropertyCache = new();
    private static readonly ConcurrentDictionary<Type, bool> ActorNameAsRoleCache = new();
    private static readonly ConcurrentDictionary<Type, bool> ExcludedTypeCache = new();

    private readonly IAuditActor _actor;
    private readonly ICorrelationContext _correlationContext;
    private readonly IAuditLogAccessor _auditLogAccessor;
    private readonly AuditSignalChannel _auditSignalChannel;
    private readonly TimeProvider _clock;
    private readonly IChronoAuditCalendar _calendar;

    private List<AuditLogEntry> _stagedAutoLogs = [];
    private bool _signalAfterCommit;

    /// <summary>Creates the context.</summary>
    /// <param name="options">The EF Core options.</param>
    /// <param name="actor">Who is making the change.</param>
    /// <param name="correlation">The id that ties the audit rows of a request together.</param>
    /// <param name="auditLogAccessor">The staged hand-written rows.</param>
    /// <param name="signal">Wakes the hash worker after a save that wrote audit rows.</param>
    /// <param name="clock">The time source; null means <see cref="TimeProvider.System"/>.</param>
    /// <param name="calendar">The calendar of <see cref="AuditLogEntry.CalendarDate"/>; null means Gregorian in UTC.</param>
    protected AuditableDbContext(
        DbContextOptions options,
        IAuditActor actor,
        ICorrelationContext correlation,
        IAuditLogAccessor auditLogAccessor,
        AuditSignalChannel signal,
        TimeProvider? clock = null,
        IChronoAuditCalendar? calendar = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(auditLogAccessor);
        ArgumentNullException.ThrowIfNull(signal);
        _actor = actor;
        _correlationContext = correlation;
        _auditLogAccessor = auditLogAccessor;
        _auditSignalChannel = signal;
        _clock = clock ?? TimeProvider.System;
        _calendar = calendar ?? new GregorianChronoAuditCalendar(TimeZoneInfo.Utc);
    }

    /// <summary>The audit table of this database.</summary>
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    /// <summary>When true, saves skip the audit pipeline. Only for data seeding; never set it on a request path.</summary>
    public bool BypassAuditingForSeed { get; set; }

    /// <summary>The knobs of this context (system actor name, delegated display-name format). Override to supply the host's values.</summary>
    protected virtual AuditTrailOptions AuditOptions => AuditTrailOptions.Default;

    /// <summary>
    /// True for an entity type that must never be audited automatically: the audit table itself and every type that implements
    /// <see cref="IExcludedFromAutoAudit"/>. A host adds its own plumbing tables (for example an outbox) by overriding this and calling the base.
    /// The answer for a type never changes, so the base check is cached per type (this runs for every tracked entity of every save).
    /// </summary>
    /// <param name="entityClrType">The CLR type of a tracked entity.</param>
    /// <returns>True when the type is not audited automatically.</returns>
    public virtual bool IsAuditExcluded(Type entityClrType)
    {
        ArgumentNullException.ThrowIfNull(entityClrType);
        return ExcludedTypeCache.GetOrAdd(
            entityClrType, static type => type == typeof(AuditLogEntry) || typeof(IExcludedFromAutoAudit).IsAssignableFrom(type));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: use <see cref="SaveChangesAsync(bool, CancellationToken)"/>.</exception>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new NotSupportedException(
            "Synchronous SaveChanges would skip the audit pipeline: use SaveChangesAsync.");

    /// <summary>The single save pipeline. <c>SaveChangesAsync(CancellationToken)</c> of <see cref="DbContext"/> forwards here with <c>true</c>.</summary>
    /// <param name="acceptAllChangesOnSuccess">Whether to accept the changes after a successful save.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of rows written, audit rows included.</returns>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Runs before the soft-delete conversion and before the seed bypass: append-only rows are never edited.
        RejectAppendOnlyMutations();
        RejectAuditLogTampering();

        // The host's hook (for example domain-event handlers) may add or change entities, so it runs before anything is stamped, audited
        // or emitted (once, never per retry).
        await OnBeforeSaveAsync(cancellationToken);
        await OnBeforeAuditAsync(cancellationToken);

        int saved;
        if (BypassAuditingForSeed)
        {
            saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        else if (!Database.IsRelational())
        {
            saved = await SaveWithAuditAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        else
        {
            var strategy = Database.CreateExecutionStrategy();
            saved = await strategy.ExecuteAsync(async () =>
            {
                var isNewTransaction = Database.CurrentTransaction is null;
                var transaction = isNewTransaction
                    ? await Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                    : Database.CurrentTransaction!;

                try
                {
                    var result = await SaveWithAuditAsync(acceptAllChangesOnSuccess, cancellationToken);
                    if (isNewTransaction)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return result;
                }
                catch
                {
                    if (isNewTransaction)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    throw;
                }
                finally
                {
                    if (isNewTransaction)
                    {
                        await transaction.DisposeAsync();
                    }
                }
            });
        }

        // Wake the hash worker only now: inside the transaction it could read before the commit and miss these rows until the next save.
        if (_signalAfterCommit)
        {
            _signalAfterCommit = false;
            _auditSignalChannel.NotifyNewAudit();
        }

        OnAfterSaved();
        return saved;
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new DisableSqlOutputClauseConvention());
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var dialect = ChronoAuditDialect.For(Database.ProviderName);
        modelBuilder.Entity<AuditLogEntry>(builder =>
        {
            builder.ToTable("AuditLogEntries", tb => tb.HasTrigger("TR_AuditLogEntries_Guard"));
            builder.HasKey(e => e.SequenceNumber);
            builder.Property(e => e.SequenceNumber).ValueGeneratedOnAdd();
            builder.HasIndex(e => e.Id).IsUnique().HasDatabaseName("UX_AuditLogEntries_Id");

            // The table is read by actor (profile activity) and by one resource's history (oldest first by sequence).
            builder.HasIndex(e => new { e.ActorUserId, e.OccurredAtUtc }).IsDescending(false, true).HasDatabaseName("IX_AuditLogEntries_Actor");
            builder.HasIndex(e => new { e.ResourceType, e.ResourceId, e.SequenceNumber }).HasDatabaseName("IX_AuditLogEntries_Resource");
            builder.HasIndex(e => new { e.OccurredAtUtc, e.SequenceNumber }).HasDatabaseName("IX_AuditLogEntries_Occurred");
            builder.HasIndex(e => new { e.CalendarDate, e.SequenceNumber }).HasDatabaseName("IX_AuditLogEntries_CalendarDate");

            // The hash worker looks for rows without a hash in sequence order: this filtered index lets it seek the few pending rows
            // instead of walking every row that is already hashed. The quoting comes from the dialect.
            builder.HasIndex(e => e.SequenceNumber).HasFilter(dialect.IsNull("PayloadHash")).HasDatabaseName("IX_AuditLogEntries_Unhashed");
            builder.HasIndex(e => e.ChainIndex).IsUnique().HasFilter(dialect.IsNotNull("ChainIndex")).HasDatabaseName("UX_AuditLogEntries_ChainIndex");

            builder.Property(e => e.CorrelationId).IsRequired();
            builder.Property(e => e.ActorUserId).HasMaxLength(64).IsRequired();
            builder.Property(e => e.Action).HasMaxLength(128).IsRequired();
            builder.Property(e => e.ResourceType).HasMaxLength(128).IsRequired();
            builder.Property(e => e.ResourceId).HasMaxLength(256).IsRequired();
            builder.Property(e => e.PreviousHash).HasMaxLength(64);
            builder.Property(e => e.PayloadHash).HasMaxLength(64);
        });

        var entities = modelBuilder.Model.GetEntityTypes()
            .Where(e => typeof(IAuditableEntity).IsAssignableFrom(e.ClrType) && e.BaseType == null);

        foreach (var entity in entities)
        {
            var parameter = Expression.Parameter(entity.ClrType, "e");
            var property = Expression.Property(parameter, nameof(IAuditableEntity.IsDeleted));
            var condition = Expression.Equal(property, Expression.Constant(false));
            var lambda = Expression.Lambda(condition, parameter);
            modelBuilder.Entity(entity.ClrType).HasQueryFilter(lambda);
        }
    }

    /// <summary>Runs first, once per save, before anything is stamped or audited (for example to dispatch domain events). Default: nothing.</summary>
    /// <param name="cancellationToken">The save's cancellation token.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    protected virtual Task OnBeforeSaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs after <see cref="OnBeforeSaveAsync"/> and before audit rows are staged (for example to emit business events). Default: nothing.</summary>
    /// <param name="cancellationToken">The save's cancellation token.</param>
    /// <returns>A task that completes when the hook is done.</returns>
    protected virtual Task OnBeforeAuditAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs once after a successful save (never on a failed attempt).</summary>
    protected virtual void OnAfterSaved()
    {
    }

    /// <summary>Resolves the name stamped on created/modified/deleted rows. A module may override it with its own lookup.</summary>
    /// <param name="userId">The actor id that is being stamped.</param>
    /// <param name="cancellationToken">The save's cancellation token.</param>
    /// <returns>The display name.</returns>
    protected virtual Task<string> ResolveUserDisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_actor.DisplayName ?? AuditOptions.SystemDisplayName);
    }

    // One attempt: an execution-strategy retry re-enters here, so the rows staged by the failed attempt are detached first.
    private async Task<int> SaveWithAuditAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        foreach (var staged in _stagedAutoLogs)
        {
            Entry(staged).State = EntityState.Detached;
        }

        _stagedAutoLogs = [];

        var now = _clock.GetUtcNow().ToUniversalTime();
        await UpdateAuditableEntitiesAsync(now, cancellationToken);
        var late = new List<LateAudit>();
        _stagedAutoLogs = GenerateAutoAuditLogs(now, late);
        var manualLogs = _auditLogAccessor.GetPendingLogs();

        // Manual logs are stable instances: after a failed attempt they are still tracked and must not be added twice.
        foreach (var manual in manualLogs.Where(m => Entry(m).State == EntityState.Detached))
        {
            manual.StampTime(now, CalendarDateOf(now));
            AuditLogEntries.Add(manual);
        }

        if (_stagedAutoLogs.Count > 0)
        {
            AuditLogEntries.AddRange(_stagedAutoLogs);
        }

        var hasAudit = _stagedAutoLogs.Count > 0 || manualLogs.Count > 0 || late.Count > 0;

        // With entities whose key the database makes, the tracker must accept this save: the second save below must not insert them again.
        var result = await base.SaveChangesAsync(late.Count > 0 || acceptAllChangesOnSuccess, cancellationToken);

        _stagedAutoLogs = [];
        if (late.Count > 0)
        {
            result += await SaveLateAuditAsync(late, now, cancellationToken);
        }

        if (hasAudit)
        {
            _auditLogAccessor.Clear();
            _signalAfterCommit = true;
        }

        return result;
    }

    // A row that depends on a value the database makes (the key of an added entity, or a foreign key that points at such a key) cannot be
    // written before the save: it would hold EF Core's placeholder, and an audit row is append-only (it cannot be corrected afterwards). So it
    // is written right after the entities, in the same transaction, with the real values.
    private async Task<int> SaveLateAuditAsync(List<LateAudit> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var calendarDate = CalendarDateOf(now);
        var rows = items.Select(item => item.Built is null
            ? BuildAutoLog(item.Entry, EntityState.Added, now, calendarDate)!
            : WithRealValues(item, now, calendarDate)).ToList();
        _stagedAutoLogs = rows;
        AuditLogEntries.AddRange(rows);

        try
        {
            return await base.SaveChangesAsync(true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            DetachLateRows(rows);
            throw;
        }
        catch (Exception ex)
        {
            DetachLateRows(rows);

            // The entities were saved and accepted already: a retry of the whole save would find nothing to write and lose them. So the
            // failure is not a transient one (the execution strategy does not retry it) and the context must be discarded.
            throw new ChronoAuditSaveException("The audit rows of new entities could not be written; the transaction is rolled back. Discard this context.", ex);
        }
    }

    // A changed entity (not a new one) whose foreign key points at a new entity with a generated key: the row was built before the save with
    // the placeholder; the placeholder is replaced by the key the database made.
    private AuditLogEntry WithRealValues(LateAudit item, DateTimeOffset now, int calendarDate)
    {
        var built = item.Built!;
        var values = JsonNode.Parse(built.NewValues ?? "{}") as JsonObject ?? new JsonObject();
        foreach (var name in item.Temporary.Where(values.ContainsKey))
        {
            values[name] = JsonSerializer.SerializeToNode(item.Entry.Property(name).CurrentValue);
        }

        var log = new AuditLogEntry
        {
            CorrelationId = built.CorrelationId,
            ActorUserId = built.ActorUserId,
            Action = built.Action,
            ResourceType = built.ResourceType,
            ResourceId = built.ResourceId,
            OldValues = built.OldValues,
            NewValues = values.ToJsonString(),
            Metadata = built.Metadata,
        };
        log.StampTime(now, calendarDate);
        return log;
    }

    // What waits for the save: the entity, the row built before the save (a changed entity; null for an added one, whose row is built after)
    // and the properties that hold a placeholder.
    private sealed record LateAudit(EntityEntry Entry, AuditLogEntry? Built, IReadOnlyList<string> Temporary);

    private void DetachLateRows(List<AuditLogEntry> rows)
    {
        foreach (var row in rows)
        {
            Entry(row).State = EntityState.Detached;
        }

        _stagedAutoLogs = [];
    }

    private int CalendarDateOf(DateTimeOffset instantUtc)
    {
        var parts = _calendar.PartsOf(instantUtc);
        return (parts.Year * 10000) + (parts.Month * 100) + parts.Day;
    }

    private void RejectAppendOnlyMutations()
    {
        // Entries<T>() runs DetectChanges, so property edits are already reflected as Modified.
        var violation = ChangeTracker.Entries<IAppendOnly>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (violation is not null)
        {
            throw new InvalidOperationException(
                $"{violation.Metadata.ClrType.Name} is append-only: {violation.State} is rejected. Record a compensating row instead.");
        }
    }

    // The audit table is evidence: the application never deletes a row and only the hash worker fills the hash columns.
    // A wrong entry is corrected with a compensating row. Runs before the seed bypass, like the append-only guard.
    private void RejectAuditLogTampering()
    {
        foreach (var entry in ChangeTracker.Entries<AuditLogEntry>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException("AuditLogEntry cannot be deleted. Record a compensating row instead.");
            }

            if (entry.State == EntityState.Modified
                && entry.Properties.Any(p => p.IsModified
                    && p.Metadata.Name is not (nameof(AuditLogEntry.PreviousHash) or nameof(AuditLogEntry.PayloadHash) or nameof(AuditLogEntry.ChainIndex))))
            {
                throw new InvalidOperationException("AuditLogEntry is append-only: only its hash columns may be filled. Record a compensating row instead.");
            }
        }
    }

    private async Task UpdateAuditableEntitiesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = AuditOptions;
        var userId = _actor.UserId ?? options.SystemUserId;
        var displayName = await ResolveUserDisplayNameAsync(userId, cancellationToken);
        if (_actor.IsDelegated)
        {
            displayName = string.Format(System.Globalization.CultureInfo.InvariantCulture, options.DelegatedDisplayNameFormat, displayName);
        }

        var actorRoleTitle = !string.IsNullOrWhiteSpace(_actor.RoleTitle)
            ? _actor.RoleTitle
            : _actor.Role;

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedBy = userId;
                var effectiveName = ActorNameAsRoleCache.GetOrAdd(entry.Entity.GetType(), static t => t.IsDefined(typeof(AuditActorNameAsRoleAttribute), true))
                                    && !string.IsNullOrWhiteSpace(actorRoleTitle)
                    ? actorRoleTitle
                    : displayName;

                entry.Entity.CreatedByName = effectiveName;
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.IsDeleted = false;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.ModifiedBy = userId;
                entry.Entity.ModifiedByName = displayName;
                entry.Entity.ModifiedAtUtc = now;

                entry.Property(x => x.CreatedBy).IsModified = false;
                entry.Property(x => x.CreatedByName).IsModified = false;
                entry.Property(x => x.CreatedAtUtc).IsModified = false;
            }
            else if (entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                entry.Entity.IsDeleted = true;

                entry.Entity.DeletedBy = userId;
                entry.Entity.DeletedByName = displayName;
                entry.Entity.DeletedAtUtc = now;

                entry.Property(x => x.CreatedBy).IsModified = false;
                entry.Property(x => x.CreatedByName).IsModified = false;
                entry.Property(x => x.CreatedAtUtc).IsModified = false;
            }
        }
    }

    private List<AuditLogEntry> GenerateAutoAuditLogs(DateTimeOffset now, List<LateAudit> late)
    {
        ChangeTracker.DetectChanges();
        var logs = new List<AuditLogEntry>();
        var calendarDate = CalendarDateOf(now);

        var entries = ChangeTracker.Entries()
            .Where(e => !IsAuditExcluded(e.Entity.GetType())
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in entries)
        {
            // A key (or a foreign key to one) is only a placeholder until the database has made it: such a row is written after the save.
            var temporary = entry.Properties.Where(p => p.IsTemporary).Select(p => p.Metadata.Name).ToList();
            if (entry.State == EntityState.Added && temporary.Count > 0)
            {
                late.Add(new LateAudit(entry, null, temporary));
                continue;
            }

            if (BuildAutoLog(entry, entry.State, now, calendarDate) is not { } log)
            {
                continue;
            }

            if (entry.State == EntityState.Modified && temporary.Count > 0)
            {
                late.Add(new LateAudit(entry, log, temporary));
                continue;
            }

            logs.Add(log);
        }

        return logs;
    }

    // The row of one entity. <paramref name="state"/> is the change to describe: for a row written after the save it is Added although the
    // tracker has accepted the entity by then. Null when a modified entity has no modified column.
    private AuditLogEntry? BuildAutoLog(EntityEntry entry, EntityState state, DateTimeOffset now, int calendarDate)
    {
        var options = AuditOptions;
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        var keyValues = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            var propertyName = property.Metadata.Name;
            var cacheKey = $"{entry.Metadata.Name}.{propertyName}";

            var isSensitive = SensitivePropertyCache.GetOrAdd(cacheKey, _ =>
                property.Metadata.PropertyInfo?.IsDefined(typeof(SensitiveDataAttribute), true) ?? false);

            if (property.Metadata.IsPrimaryKey())
            {
                keyValues[propertyName] = property.CurrentValue;
            }

            var valueToStore = isSensitive ? MaskedValue : property.CurrentValue;
            var originalValueToStore = isSensitive ? MaskedValue : property.OriginalValue;

            if (state == EntityState.Added)
            {
                newValues[propertyName] = valueToStore;
            }
            else if (state == EntityState.Modified && property.IsModified)
            {
                oldValues[propertyName] = originalValueToStore;
                newValues[propertyName] = valueToStore;
            }
            else if (state == EntityState.Deleted)
            {
                // Physical delete (only entities that are not soft-deleted reach here): keep the whole last state, masked like any other row.
                oldValues[propertyName] = originalValueToStore;
            }
        }

        if (state == EntityState.Modified && oldValues.Count == 0)
        {
            return null;
        }

        var isSoftDelete = state == EntityState.Modified
                           && entry.Entity is IAuditableEntity auditable
                           && auditable.IsDeleted
                           && entry.Property(nameof(IAuditableEntity.IsDeleted)).OriginalValue?.Equals(false) == true;

        // The host's own metadata first (for example who acted on behalf of whom), then Ip and SessionId. Additive keys only.
        var metadataNode = _actor.AuditMetadata is { } extra
            ? JsonSerializer.SerializeToNode(extra) as JsonObject ?? new JsonObject()
            : new JsonObject();
        if (!string.IsNullOrWhiteSpace(_actor.UserIp))
        {
            metadataNode["Ip"] = _actor.UserIp;
        }

        if (!string.IsNullOrWhiteSpace(_actor.SessionId))
        {
            metadataNode["SessionId"] = _actor.SessionId;
        }

        var auditMetadata = metadataNode.Count == 0 ? null : metadataNode.ToJsonString();

        var log = new AuditLogEntry
        {
            CorrelationId = _correlationContext.CorrelationId,
            ActorUserId = _actor.ActorUserId ?? _actor.UserId ?? options.SystemUserId,
            Action = isSoftDelete ? AuditActions.AutoSoftDeleted : $"Auto_{state}",
            ResourceType = entry.Metadata.GetTableName() ?? entry.Entity.GetType().Name,
            ResourceId = JsonSerializer.Serialize(keyValues),
            OldValues = oldValues.Count == 0 ? null : JsonSerializer.Serialize(oldValues),
            NewValues = newValues.Count == 0 ? null : JsonSerializer.Serialize(newValues),
            Metadata = auditMetadata,
        };
        log.StampTime(now, calendarDate);
        return log;
    }
}

/// <summary>
/// A model convention that turns off the SQL Server <c>OUTPUT</c> clause on every entity. Tables that carry triggers (monitoring or tamper
/// guards on insert, update or delete) make EF fail with SQL Server error 334 when it uses <c>OUTPUT</c>; without the clause every save
/// works whether or not a trigger exists.
/// </summary>
public sealed class DisableSqlOutputClauseConvention : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        Microsoft.EntityFrameworkCore.Metadata.Builders.IConventionModelBuilder modelBuilder,
        IConventionContext<Microsoft.EntityFrameworkCore.Metadata.Builders.IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            entityType.SetAnnotation("SqlServer:UseSqlOutputClause", false);
        }
    }
}
