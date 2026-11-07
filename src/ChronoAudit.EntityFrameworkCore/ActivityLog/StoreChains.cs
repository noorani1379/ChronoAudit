using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Names of the two store chains: also their module names for the anchor worker and chain verification.</summary>
internal static class StoreChainModules
{
    public const string Activity = "chronoaudit-activity";
    public const string Intents = "chronoaudit-intents";
}

/// <summary>Reads the key-rotation markers of a store chain from the audit table of the first registered audited module.</summary>
internal sealed class StoreMarkerReader(ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory)
{
    public bool HasAuditedModule => registry.Modules.Count > 0;

    public async Task<List<ChainMarker>> ReadAsync(string chainModule, bool includePending, CancellationToken cancellationToken)
    {
        var markers = new List<ChainMarker>();
        if (!HasAuditedModule)
        {
            return markers;
        }

        using var scope = scopeFactory.CreateScope();
        var db = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(registry.Modules[0].ContextType);
        var hashed = await db.AuditLogEntries.AsNoTracking()
            .Where(x => x.Action == AuditActions.KeyRotated && x.ResourceId == chainModule && x.ChainIndex != null)
            .OrderBy(x => x.ChainIndex)
            .Select(x => x.Metadata)
            .ToListAsync(cancellationToken);
        foreach (var metadata in hashed)
        {
            if (ChainMarker.TryParse(metadata, out var marker))
            {
                markers.Add(marker);
            }
        }

        if (includePending)
        {
            var pending = await db.AuditLogEntries.AsNoTracking()
                .Where(x => x.PayloadHash == null && x.Action == AuditActions.KeyRotated && x.ResourceId == chainModule)
                .OrderBy(x => x.SequenceNumber)
                .Select(x => x.Metadata)
                .FirstOrDefaultAsync(cancellationToken);
            if (pending is not null && ChainMarker.TryParse(pending, out var pendingMarker))
            {
                markers.Add(pendingMarker);
            }
        }

        return markers;
    }
}

/// <summary>
/// The tamper-evidence chain of a store table (request log or mutation intents): the same HMAC key ring as the audit chain, every row
/// carries the hash of the row hashed before it in <c>ChainIndex</c> order, and the hash covers every column of the row (and of its body
/// row). Writers are serialized by the chain lock in a ReadCommitted transaction; a row that commits late simply joins at the end.
/// </summary>
internal sealed class StoreChain<TRow>(
    AuditKeyRing keyRing,
    StoreMarkerReader markers,
    string module,
    Func<IQueryable<TRow>, IQueryable<TRow>> withDependents,
    Func<TRow, IReadOnlyList<string?>> fieldsOf,
    Action<TRow, long, string?, string> setHash,
    ILogger logger)
    where TRow : class
{
    public string Module => module;

    private string LockName => "chronoaudit:" + (module == StoreChainModules.Activity ? "activity" : "intents");

    public async Task<ChainHead?> GetHeadAsync(DbContext db, CancellationToken cancellationToken)
    {
        var head = await db.Set<TRow>().AsNoTracking()
            .Where(x => EF.Property<long?>(x, "ChainIndex") != null)
            .OrderByDescending(x => EF.Property<long?>(x, "ChainIndex"))
            .Select(x => new { Index = EF.Property<long?>(x, "ChainIndex"), Id = EF.Property<long>(x, "Id"), Hash = EF.Property<string?>(x, "PayloadHash") })
            .FirstOrDefaultAsync(cancellationToken);
        return head is null ? null : new ChainHead(head.Index!.Value, head.Id, head.Hash!);
    }

    public async Task<AnchorCheck> VerifyAnchorsAsync(DbContext db, IReadOnlyCollection<AuditAnchor> anchors, CancellationToken cancellationToken)
    {
        if (anchors.Count == 0)
        {
            return AnchorCheck.None;
        }

        var found = new Dictionary<long, string?>();
        foreach (var chunk in anchors.Select(a => a.ChainIndex).Distinct().Chunk(500))
        {
            var rows = await db.Set<TRow>().AsNoTracking()
                .Where(x => chunk.Contains(EF.Property<long?>(x, "ChainIndex")!.Value))
                .Select(x => new { Index = EF.Property<long?>(x, "ChainIndex"), Hash = EF.Property<string?>(x, "PayloadHash") })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                found[row.Index!.Value] = row.Hash;
            }
        }

        var mismatches = anchors
            .Where(a => !found.TryGetValue(a.ChainIndex, out var hash) || !string.Equals(hash, a.PayloadHash, StringComparison.Ordinal))
            .Select(a => a.ChainIndex).Distinct().Order().ToList();
        return new AnchorCheck(anchors.Count, mismatches);
    }

    /// <summary>Hashes every pending row in batches. Returns the number hashed, or -1 when the key guard stopped the hashing.</summary>
    public async Task<int> HashAllPendingAsync(DbContext db, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var hashed = await HashBatchAsync(db, batchSize, cancellationToken);
            if (hashed < 0)
            {
                return -1;
            }

            if (hashed == 0)
            {
                break;
            }

            total += hashed;
            db.ChangeTracker.Clear();
        }

        return total;
    }

    public async Task<AuditChainVerification> VerifyAsync(
        DbContext db, CancellationToken cancellationToken, int batchSize = 1000, long afterChainIndex = 0, int maxRows = int.MaxValue)
    {
        // A rotation marker counts as soon as it is saved: the audit hash worker may not have hashed it yet.
        using var segments = new SegmentKeys(keyRing, await markers.ReadAsync(module, includePending: true, cancellationToken));
        string? expectedPrevious = null;
        var last = Math.Max(afterChainIndex, 0);
        long verified = 0;
        long examined = 0;

        if (last > 0)
        {
            expectedPrevious = await db.Set<TRow>().AsNoTracking()
                .Where(x => EF.Property<long?>(x, "ChainIndex") == last)
                .Select(x => EF.Property<string?>(x, "PayloadHash"))
                .FirstOrDefaultAsync(cancellationToken);
            if (expectedPrevious is null)
            {
                return new AuditChainVerification(false, 0, await PendingAsync(db, cancellationToken), last + 1, null, AuditChainFailure.BrokenLink);
            }
        }

        while (examined < maxRows)
        {
            var batch = await withDependents(db.Set<TRow>().AsNoTracking())
                .Where(x => EF.Property<long?>(x, "ChainIndex") > last)
                .OrderBy(x => EF.Property<long?>(x, "ChainIndex"))
                .Take((int)Math.Min(batchSize, maxRows - examined))
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                examined++;
                var entry = db.Entry(row);
                var index = entry.Property<long?>("ChainIndex").CurrentValue!.Value;
                var id = entry.Property<long>("Id").CurrentValue;
                if (index != last + 1)
                {
                    return new AuditChainVerification(false, verified, await PendingAsync(db, cancellationToken), last + 1, null, AuditChainFailure.BrokenLink);
                }

                last = index;
                var previous = entry.Property<string?>("PreviousHash").CurrentValue;
                var hash = entry.Property<string?>("PayloadHash").CurrentValue;
                if (!string.Equals(previous, expectedPrevious, StringComparison.Ordinal))
                {
                    return new AuditChainVerification(false, verified, await PendingAsync(db, cancellationToken), index, id, AuditChainFailure.BrokenLink);
                }

                if (!segments.TryGetKey(index, out var key))
                {
                    return new AuditChainVerification(false, verified, await PendingAsync(db, cancellationToken), index, id, AuditChainFailure.UnknownKey);
                }

                using var hasher = new FieldChainHasher(key);
                if (!string.Equals(hasher.Compute(previous, fieldsOf(row)), hash, StringComparison.Ordinal))
                {
                    return new AuditChainVerification(false, verified, await PendingAsync(db, cancellationToken), index, id, AuditChainFailure.HashMismatch);
                }

                expectedPrevious = hash;
                verified++;
            }
        }

        var more = examined >= maxRows && await db.Set<TRow>().AsNoTracking().AnyAsync(x => EF.Property<long?>(x, "ChainIndex") > last, cancellationToken);
        return new AuditChainVerification(true, verified, await PendingAsync(db, cancellationToken), null, null, null, more ? last : null);
    }

    /// <summary>The fingerprint of the ring key that verifies the newest hashed row, or null when none does. Used by the rotation.</summary>
    public async Task<(long ChainIndex, string? Fingerprint)?> FindHeadKeyAsync(DbContext db, CancellationToken cancellationToken)
    {
        var head = await withDependents(db.Set<TRow>().AsNoTracking())
            .Where(x => EF.Property<long?>(x, "ChainIndex") != null)
            .OrderByDescending(x => EF.Property<long?>(x, "ChainIndex"))
            .FirstOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }

        var entry = db.Entry(head);
        var index = entry.Property<long?>("ChainIndex").CurrentValue!.Value;
        var previous = entry.Property<string?>("PreviousHash").CurrentValue;
        var hash = entry.Property<string?>("PayloadHash").CurrentValue;
        foreach (var fingerprint in keyRing.Fingerprints)
        {
            keyRing.TryGet(fingerprint, out var key);
            using var hasher = new FieldChainHasher(key);
            if (string.Equals(hasher.Compute(previous, fieldsOf(head)), hash, StringComparison.Ordinal))
            {
                return (index, fingerprint);
            }
        }

        return (index, null);
    }

    /// <summary>The chain lock of this chain, in an open ReadCommitted transaction on the store context (used by the rotation).</summary>
    public Task AcquireLockAsync(DbContext db, CancellationToken cancellationToken) => ChronoAuditDialect.For(db).AcquireChainLockAsync(db, LockName, cancellationToken);

    private static Task<long> PendingAsync(DbContext db, CancellationToken cancellationToken)
        => db.Set<TRow>().AsNoTracking().LongCountAsync(x => EF.Property<string?>(x, "PayloadHash") == null, cancellationToken);

    private async Task<int> HashBatchAsync(DbContext db, int batchSize, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            return await HashCoreAsync(db, batchSize, cancellationToken);
        }

        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await AcquireLockAsync(db, cancellationToken);
                var count = await HashCoreAsync(db, batchSize, cancellationToken);
                if (count < 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return count;
                }

                await transaction.CommitAsync(cancellationToken);
                return count;
            });
        }
        catch (DbUpdateException ex) when (ChronoAuditDialect.For(db).IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            logger.LogWarning("Two hash workers raced on {Module}; run one worker per database or use a supported provider.", module);
            return 0;
        }
    }

    private async Task<int> HashCoreAsync(DbContext db, int batchSize, CancellationToken cancellationToken)
    {
        var headQuery = withDependents(db.Set<TRow>().AsNoTracking())
            .Where(x => EF.Property<long?>(x, "ChainIndex") != null)
            .OrderByDescending(x => EF.Property<long?>(x, "ChainIndex"));
        var head = await headQuery.FirstOrDefaultAsync(cancellationToken);

        var pending = await withDependents(db.Set<TRow>())
            .Where(x => EF.Property<string?>(x, "PayloadHash") == null)
            .OrderBy(x => EF.Property<long>(x, "Id"))
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        using var segments = new SegmentKeys(keyRing, await markers.ReadAsync(module, includePending: true, cancellationToken));
        string? previousHash = null;
        long chainIndex = 0;
        if (head is not null)
        {
            var entry = db.Entry(head);
            chainIndex = entry.Property<long?>("ChainIndex").CurrentValue!.Value;
            previousHash = entry.Property<string?>("PayloadHash").CurrentValue;
            var headPrevious = entry.Property<string?>("PreviousHash").CurrentValue;

            // The key guard: the newest hashed row must verify with the key of its own segment.
            var verifies = segments.TryGetKey(chainIndex, out var headKey);
            if (verifies)
            {
                using var headHasher = new FieldChainHasher(headKey);
                verifies = string.Equals(headHasher.Compute(headPrevious, fieldsOf(head)), previousHash, StringComparison.Ordinal);
            }

            if (!verifies)
            {
                logger.LogCritical(
                    "Audit chain of {Module} was hashed with another key: the configured keys do not verify chain index {ChainIndex}. Hashing stopped; add the old key to ChronoAudit:PreviousHmacKeys and call RotateKeyAsync.",
                    module,
                    chainIndex);
                return -1;
            }
        }

        foreach (var row in pending)
        {
            chainIndex++;
            if (!segments.TryGetKey(chainIndex, out var key))
            {
                logger.LogCritical("Audit chain of {Module} needs a key that is not configured for chain index {ChainIndex}. Hashing stopped.", module, chainIndex);
                return -1;
            }

            using var hasher = new FieldChainHasher(key);
            var hash = hasher.Compute(previousHash, fieldsOf(row));
            setHash(row, chainIndex, previousHash, hash);
            previousHash = hash;
        }

        await db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }
}

/// <summary>The pinned field orders of the two store chains (changing one invalidates every stored chain of that table: trap T7).</summary>
internal static class StoreLayouts
{
    public const string ActivityLayout = "chronoaudit-activity-v1";
    public const string IntentLayout = "chronoaudit-intent-v1";

    public static IReadOnlyList<string?> Fields(ApiRequestLog log) =>
    [
        ActivityLayout,
        log.Id.ToString(CultureInfo.InvariantCulture),
        log.RequestId.ToString("D"),
        log.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture),
        log.DurationMs.ToString(CultureInfo.InvariantCulture),
        log.UserId,
        log.ActAsUserId,
        log.SessionId,
        log.CorrelationId?.ToString("D"),
        log.CalendarYear?.ToString(CultureInfo.InvariantCulture),
        log.CalendarMonth?.ToString(CultureInfo.InvariantCulture),
        log.CalendarDay?.ToString(CultureInfo.InvariantCulture),
        log.CalendarQuarter?.ToString(CultureInfo.InvariantCulture),
        log.HttpMethod,
        log.Path,
        log.EndpointKey,
        log.QueryString,
        log.StatusCode.ToString(CultureInfo.InvariantCulture),
        log.ErrorCode,
        log.IpAddress,
        log.UserAgent,
        log.HasBody ? "1" : "0",
        log.Body?.ContentType,
        log.Body is null ? null : (log.Body.IsTruncated ? "1" : "0"),
        log.Body?.Body,
        log.Body?.FileMetadata,
    ];

    public static IReadOnlyList<string?> Fields(ApiMutationIntent intent) =>
    [
        IntentLayout,
        intent.Id.ToString(CultureInfo.InvariantCulture),
        intent.RequestId.ToString("D"),
        intent.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        intent.UserId,
        intent.ActAsUserId,
        intent.SessionId,
        intent.CorrelationId?.ToString("D"),
        intent.Method,
        intent.Path,
        intent.EndpointKey,
        intent.BodySha256,
        intent.BodyLength.ToString(CultureInfo.InvariantCulture),
    ];
}

/// <summary>The chain source of a store chain, so the anchor worker and chain verification treat it like an audit module.</summary>
internal sealed class StoreChainSource<TRow>(StoreChain<TRow> chain, ChronoAuditStoreRegistration registration, IServiceScopeFactory scopeFactory) : IAuditChainSource
    where TRow : class
{
    public string Module => chain.Module;

    public async Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await chain.VerifyAsync((DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType), cancellationToken);
    }

    public async Task<AuditChainVerification> VerifyRangeAsync(long afterChainIndex, int maxRows, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await chain.VerifyAsync((DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType), cancellationToken, afterChainIndex: afterChainIndex, maxRows: maxRows);
    }

    public async Task<AnchorCheck> VerifyAnchorsAsync(IReadOnlyCollection<AuditAnchor> anchors, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await chain.VerifyAnchorsAsync((DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType), anchors, cancellationToken);
    }

    public async Task<ChainHead?> GetHeadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await chain.GetHeadAsync((DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType), cancellationToken);
    }
}

/// <summary>
/// Extends the hash chains of the request log and of the mutation intents. The tables grow with every API call, so unlike the audit
/// worker it is not woken per write: it looks at a fixed interval (the filtered index on unhashed rows makes an idle look one cheap seek)
/// and hashes what it finds in batches. A failing round is logged and retried at the next interval.
/// </summary>
internal sealed class ActivityHashWorker(
    ChronoAuditStoreRegistration registration,
    IServiceScopeFactory scopeFactory,
    StoreChain<ApiRequestLog> requestChain,
    StoreChain<ApiMutationIntent> intentChain,
    ActivityLogOptions options,
    ILogger<ActivityHashWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HashIntervalSeconds));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One round; returns the number of rows hashed, or -1 when the round failed or the key guard stopped it.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            var logs = await requestChain.HashAllPendingAsync(db, options.HashBatchSize, cancellationToken);
            var intents = await intentChain.HashAllPendingAsync(db, options.HashBatchSize, cancellationToken);
            if (logs < 0 || intents < 0)
            {
                return -1;
            }

            var hashed = logs + intents;
            if (hashed > 0)
            {
                logger.LogDebug("Hashed {HashedCount} API activity rows.", hashed);
            }

            return hashed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to hash the API activity rows. Retrying in {RetrySeconds} seconds.", options.HashIntervalSeconds);
            return -1;
        }
    }
}
