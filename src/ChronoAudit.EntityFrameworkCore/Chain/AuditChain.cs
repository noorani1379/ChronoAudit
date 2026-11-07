using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Tamper-evidence for one module's <c>AuditLogEntries</c> table: an HMAC-SHA256 chain in which every row carries the hash of the row
/// hashed before it. <see cref="HashAllPendingAsync"/> extends the chain and <see cref="VerifyAsync"/> re-walks it. Each table has its
/// own chain, so any <see cref="AuditableDbContext"/> can be passed in. The byte layout of the hashed fields must never change: it
/// would invalidate every existing chain (it is pinned by a test).
/// </summary>
/// <remarks>
/// The chain is ordered by <see cref="AuditLogEntry.ChainIndex"/>, not by the identity key, so a row that commits late simply joins at
/// the end. Writers are serialized by an application lock inside a ReadCommitted transaction. Deleting the newest hashed rows cannot
/// be detected without an external anchor; database permissions are the control for that.
/// </remarks>
public sealed class AuditChain
{
    private const int DefaultBatchSize = 500;
    private static readonly string[] MarkerFields = ChainMarker.Fields;

    private readonly AuditKeyRing _keyRing;
    private readonly ILogger _logger;

    /// <summary>Creates the chain over a key ring.</summary>
    /// <param name="keyRing">The current key and the keys of earlier segments.</param>
    /// <param name="logger">Where the key guard and race warnings go; optional.</param>
    public AuditChain(AuditKeyRing keyRing, ILogger<AuditChain>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        _keyRing = keyRing;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Creates the chain over a single key (no earlier segments).</summary>
    /// <param name="hmacSecretKey">The HMAC key.</param>
    public AuditChain(byte[] hmacSecretKey)
        : this(new AuditKeyRing(hmacSecretKey))
    {
    }

    private static ReadOnlySpan<byte> Separator => "|"u8;

    /// <summary>The key ring this chain hashes and verifies with.</summary>
    public AuditKeyRing KeyRing => _keyRing;

    /// <summary>
    /// Hashes every row that has no hash yet, in sequence order, in batches (default 500). Returns the number of rows hashed, or -1 when
    /// the key guard stopped the hashing because the configured keys do not verify the newest hashed row.
    /// </summary>
    /// <param name="db">The audit table's context.</param>
    /// <param name="batchSize">Rows per transaction.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="module">The module name (used for the lock name and the logs); defaults to the context type name.</param>
    /// <returns>The number of rows hashed, or -1 when hashing was stopped.</returns>
    public async Task<int> HashAllPendingAsync(AuditableDbContext db, int batchSize, CancellationToken cancellationToken, string? module = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        module ??= db.GetType().Name;

        var total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var hashed = await HashPendingBatchAsync(db, module, batchSize, cancellationToken);
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

    /// <summary>
    /// Walks the hashed rows in chain order. Rows without a hash yet are counted as pending (they are normal at the tail). Stops at the
    /// first row whose predecessor link or recomputed hash is wrong, or whose key is not in the key ring.
    /// </summary>
    /// <param name="db">The audit table's context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="batchSize">How many rows are read per query.</param>
    /// <param name="afterChainIndex">Resume after this chain index (0 = from the start). The link of the first row is checked against the hash of that row.</param>
    /// <param name="maxRows">Stop after this many rows; <see cref="AuditChainVerification.NextAfterChainIndex"/> then says where to continue.</param>
    /// <param name="module">The module name: only rotation markers written for this module count; null reads every marker in the table.</param>
    /// <returns>The outcome.</returns>
    public async Task<AuditChainVerification> VerifyAsync(
        AuditableDbContext db, CancellationToken cancellationToken, int batchSize = 2000, long afterChainIndex = 0, int maxRows = int.MaxValue, string? module = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var segments = new SegmentKeys(_keyRing, await ReadMarkersAsync(db, includePending: false, module, cancellationToken));
        try
        {
            string? expectedPrevious = null;
            var last = 0L;
            long verified = 0;
            long examined = 0;

            if (afterChainIndex > 0)
            {
                last = afterChainIndex;
                expectedPrevious = await db.AuditLogEntries
                    .AsNoTracking()
                    .Where(x => x.ChainIndex == afterChainIndex)
                    .Select(x => x.PayloadHash)
                    .FirstOrDefaultAsync(cancellationToken);
                if (expectedPrevious is null)
                {
                    return Broken(verified, await CountPendingAsync(db, cancellationToken), afterChainIndex + 1, null, AuditChainFailure.BrokenLink);
                }
            }

            while (examined < maxRows)
            {
                var batch = await db.AuditLogEntries
                    .AsNoTracking()
                    .Where(x => x.ChainIndex > last)
                    .OrderBy(x => x.ChainIndex)
                    .Take((int)Math.Min(batchSize, maxRows - examined))
                    .ToListAsync(cancellationToken);

                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var entry in batch)
                {
                    examined++;
                    var index = entry.ChainIndex!.Value;
                    if (index != last + 1)
                    {
                        // A missing index is a deleted (or never hashed) row in the middle of the chain.
                        return Broken(verified, await CountPendingAsync(db, cancellationToken), last + 1, null, AuditChainFailure.BrokenLink);
                    }

                    last = index;
                    if (!string.Equals(entry.PreviousHash, expectedPrevious, StringComparison.Ordinal))
                    {
                        return Broken(verified, await CountPendingAsync(db, cancellationToken), index, entry.SequenceNumber, AuditChainFailure.BrokenLink);
                    }

                    if (!segments.TryGetHmac(index, out var hmac))
                    {
                        return Broken(verified, await CountPendingAsync(db, cancellationToken), index, entry.SequenceNumber, AuditChainFailure.UnknownKey);
                    }

                    if (!string.Equals(ComputeHash(entry, entry.PreviousHash, hmac), entry.PayloadHash, StringComparison.Ordinal))
                    {
                        return Broken(verified, await CountPendingAsync(db, cancellationToken), index, entry.SequenceNumber, AuditChainFailure.HashMismatch);
                    }

                    expectedPrevious = entry.PayloadHash;
                    verified++;
                }
            }

            var more = examined >= maxRows && await db.AuditLogEntries.AsNoTracking().AnyAsync(x => x.ChainIndex > last, cancellationToken);
            return new AuditChainVerification(true, verified, await CountPendingAsync(db, cancellationToken), null, null, null, more ? last : null);
        }
        finally
        {
            segments.Dispose();
        }
    }

    /// <summary>
    /// A hasher for chains over other tables (the API activity log) that uses the current key. It is independent of the audit byte
    /// layout above: each field is written as <c>length;bytes</c> (a missing one as <c>-1;</c>), so a value that contains a separator can
    /// never shift the boundary of the next field. Dispose it when the batch is done.
    /// </summary>
    /// <returns>A new hasher.</returns>
    public FieldChainHasher CreateFieldHasher() => new(_keyRing.Current);

    /// <summary>The newest hashed row: what an anchor records. Null when nothing is hashed yet.</summary>
    /// <param name="db">The audit table's context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The head, or null.</returns>
    public async Task<(long ChainIndex, long Sequence, string PayloadHash)?> GetHeadAsync(AuditableDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var head = await db.AuditLogEntries
            .AsNoTracking()
            .Where(x => x.ChainIndex != null)
            .OrderByDescending(x => x.ChainIndex)
            .Select(x => new { x.ChainIndex, x.SequenceNumber, x.PayloadHash })
            .FirstOrDefaultAsync(cancellationToken);

        return head is null ? null : (head.ChainIndex!.Value, head.SequenceNumber, head.PayloadHash!);
    }

    /// <summary>Every anchor of this table must still find its row with the same hash; a missing row or another hash is a mismatch (deleted tail, rewritten history).</summary>
    /// <param name="db">The audit table's context.</param>
    /// <param name="anchors">The anchors of this module.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many anchors were checked and which chain indexes no longer match.</returns>
    public async Task<AnchorCheck> VerifyAnchorsAsync(AuditableDbContext db, IReadOnlyCollection<AuditAnchor> anchors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(anchors);
        if (anchors.Count == 0)
        {
            return AnchorCheck.None;
        }

        var found = new Dictionary<long, string?>();

        // Chunked so the IN list stays far below the SQL Server parameter limit.
        foreach (var chunk in anchors.Select(a => a.ChainIndex).Distinct().Chunk(500))
        {
            var rows = await db.AuditLogEntries
                .AsNoTracking()
                .Where(x => chunk.Contains(x.ChainIndex!.Value))
                .Select(x => new { x.ChainIndex, x.PayloadHash })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                found[row.ChainIndex!.Value] = row.PayloadHash;
            }
        }

        var mismatches = anchors
            .Where(a => !found.TryGetValue(a.ChainIndex, out var hash) || !string.Equals(hash, a.PayloadHash, StringComparison.Ordinal))
            .Select(a => a.ChainIndex)
            .Distinct()
            .Order()
            .ToList();

        return new AnchorCheck(anchors.Count, mismatches);
    }

    internal static string ComputeHash(AuditLogEntry log, string? previousHash, IncrementalHash hmac)
    {
        AppendStringToHash(previousHash, hmac);
        AppendLongToHash(log.SequenceNumber, hmac);
        AppendGuidToHash(log.CorrelationId, hmac);
        AppendStringToHash(log.ActorUserId, hmac);
        AppendStringToHash(log.Action, hmac);
        AppendStringToHash(log.ResourceType, hmac);
        AppendStringToHash(log.ResourceId, hmac);
        AppendStringToHash(log.OldValues, hmac);
        AppendStringToHash(log.NewValues, hmac);
        AppendStringToHash(log.Metadata, hmac);
        AppendDateToHash(log.OccurredAtUtc, hmac);

        Span<byte> hashBytes = stackalloc byte[32];
        hmac.TryGetHashAndReset(hashBytes, out _);
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Adds a key-rotation marker so the next hashing round continues with the current key. Runs inside the chain lock and a ReadCommitted
    /// transaction on relational providers. Used by the maintenance service.
    /// </summary>
    internal async Task<ChronoAuditResult> RotateKeyAsync(
        AuditableDbContext db,
        string module,
        string systemUserId,
        TimeProvider clock,
        IChronoAuditCalendar calendar,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        if (!db.Database.IsRelational())
        {
            return await RotateCoreAsync(db, module, systemUserId, clock, calendar, cancellationToken);
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            await ChronoAuditDialect.For(db).AcquireChainLockAsync(db, LockName(module), cancellationToken);
            var result = await RotateCoreAsync(db, module, systemUserId, clock, calendar, cancellationToken);
            if (result.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        });
    }

    internal static string LockName(string module) => "chronoaudit:audit:" + module;

    private static AuditChainVerification Broken(long verified, long pending, long chainIndex, long? sequence, AuditChainFailure failure)
        => new(false, verified, pending, chainIndex, sequence, failure, null);

    private static Task<long> CountPendingAsync(AuditableDbContext db, CancellationToken cancellationToken)
        => db.AuditLogEntries.AsNoTracking().LongCountAsync(x => x.PayloadHash == null, cancellationToken);

    private static async Task<List<ChainMarker>> ReadMarkersAsync(AuditableDbContext db, bool includePending, string? module, CancellationToken cancellationToken)
    {
        var hashed = await db.AuditLogEntries.AsNoTracking()
            .Where(x => x.Action == AuditActions.KeyRotated && x.ChainIndex != null && (module == null || x.ResourceId == module))
            .OrderBy(x => x.ChainIndex)
            .Select(x => x.Metadata)
            .ToListAsync(cancellationToken);

        var markers = new List<ChainMarker>();
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
                .Where(x => x.PayloadHash == null && x.Action == AuditActions.KeyRotated && (module == null || x.ResourceId == module))
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

    private bool HeadVerifiesWith(AuditLogEntry head, string fingerprint)
    {
        if (!_keyRing.TryGet(fingerprint, out var key))
        {
            return false;
        }

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        return string.Equals(ComputeHash(head, head.PreviousHash, hmac), head.PayloadHash, StringComparison.Ordinal);
    }

    private async Task<int> HashPendingBatchAsync(AuditableDbContext db, string module, int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            if (!db.Database.IsRelational())
            {
                return await HashBatchCoreAsync(db, module, batchSize, cancellationToken);
            }

            // ReadCommitted plus an application lock: no Serializable scan that blocks readers (PLAN D6). A row that is still uncommitted is
            // simply not seen and joins the chain in a later round, at the end (chain index), so the order stays gap-free.
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await ChronoAuditDialect.For(db).AcquireChainLockAsync(db, LockName(module), cancellationToken);
                var count = await HashBatchCoreAsync(db, module, batchSize, cancellationToken);
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
            _logger.LogWarning("Two hash workers raced on {Module}; run one worker per database or use a supported provider.", module);
            return 0;
        }
    }

    private async Task<int> HashBatchCoreAsync(AuditableDbContext db, string module, int batchSize, CancellationToken cancellationToken)
    {
        var head = await db.AuditLogEntries
            .AsNoTracking()
            .Where(x => x.ChainIndex != null)
            .OrderByDescending(x => x.ChainIndex)
            .FirstOrDefaultAsync(cancellationToken);

        var pending = await db.AuditLogEntries
            .Where(x => x.PayloadHash == null)
            .OrderBy(x => x.SequenceNumber)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        using var segments = new SegmentKeys(_keyRing, await ReadMarkersAsync(db, includePending: true, module, cancellationToken));

        // The key guard: the newest hashed row must verify with the key its segment names. Otherwise the key was changed without a
        // rotation, and hashing on would mix two keys in one segment (the failure that destroyed the source project's chain).
        if (head is not null)
        {
            var headIndex = head.ChainIndex!.Value;
            var fingerprint = segments.FingerprintFor(headIndex);
            if (!HeadVerifiesWith(head, fingerprint))
            {
                _logger.LogCritical(
                    "Audit chain of {Module} was hashed with another key: the configured keys do not verify chain index {ChainIndex}. Hashing stopped; add the old key to ChronoAudit:PreviousHmacKeys and call RotateKeyAsync.",
                    module,
                    headIndex);
                return -1;
            }
        }

        var previousHash = head?.PayloadHash;
        var chainIndex = head?.ChainIndex ?? 0;
        foreach (var log in pending)
        {
            chainIndex++;
            if (!segments.TryGetHmac(chainIndex, out var hmac))
            {
                _logger.LogCritical(
                    "Audit chain of {Module} needs a key that is not configured for chain index {ChainIndex}. Hashing stopped.",
                    module,
                    chainIndex);
                return -1;
            }

            var payloadHash = ComputeHash(log, previousHash, hmac);
            log.SetHash(chainIndex, previousHash, payloadHash);
            previousHash = payloadHash;
        }

        await db.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }

    private async Task<ChronoAuditResult> RotateCoreAsync(
        AuditableDbContext db, string module, string systemUserId, TimeProvider clock, IChronoAuditCalendar calendar, CancellationToken cancellationToken)
    {
        var head = await db.AuditLogEntries.AsNoTracking().Where(x => x.ChainIndex != null).OrderByDescending(x => x.ChainIndex).FirstOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Conflict(
                "Chain.NothingToRotate", "The chain has no hashed row yet; the current key will be used from the start."));
        }

        if (await db.AuditLogEntries.AsNoTracking().AnyAsync(x => x.PayloadHash == null && x.Action == AuditActions.KeyRotated, cancellationToken))
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Conflict("Chain.RotationPending", "A key rotation is already waiting to be hashed."));
        }

        using var segments = new SegmentKeys(_keyRing, await ReadMarkersAsync(db, includePending: false, module, cancellationToken));
        var fingerprint = segments.FingerprintFor(head.ChainIndex!.Value);
        if (!HeadVerifiesWith(head, fingerprint))
        {
            fingerprint = FindVerifyingFingerprint(head);
            if (fingerprint is null)
            {
                return ChronoAuditResult.Failure(ChronoAuditError.Conflict(
                    "Chain.UnknownKey", "No configured key verifies the newest hashed row; add the old key to ChronoAudit:PreviousHmacKeys."));
            }
        }

        if (string.Equals(fingerprint, _keyRing.CurrentFingerprint, StringComparison.Ordinal))
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Conflict("Chain.NothingToRotate", "The chain already uses the current key."));
        }

        var now = clock.GetUtcNow();
        var parts = calendar.PartsOf(now);
        var marker = new AuditLogEntry
        {
            CorrelationId = Guid.NewGuid(),
            ActorUserId = systemUserId,
            Action = AuditActions.KeyRotated,
            ResourceType = "ChronoAudit.Chain",
            ResourceId = module,
            Metadata = new JsonObject
            {
                [MarkerFields[0]] = fingerprint,
                [MarkerFields[1]] = _keyRing.CurrentFingerprint,
                [MarkerFields[2]] = head.ChainIndex!.Value,
            }.ToJsonString(),
        };
        marker.StampTime(now, (parts.Year * 10000) + (parts.Month * 100) + parts.Day);
        db.AuditLogEntries.Add(marker);
        await db.SaveChangesAsync(cancellationToken);
        return ChronoAuditResult.Success();
    }

    private string? FindVerifyingFingerprint(AuditLogEntry head)
    {
        foreach (var fingerprint in _keyRing.Fingerprints)
        {
            if (HeadVerifiesWith(head, fingerprint))
            {
                return fingerprint;
            }
        }

        return null;
    }

    private static void AppendStringToHash(string? text, IncrementalHash hmac)
    {
        if (string.IsNullOrEmpty(text))
        {
            hmac.AppendData(Separator);
            return;
        }

        var maxByteCount = Encoding.UTF8.GetMaxByteCount(text.Length);

        if (maxByteCount <= 512)
        {
            Span<byte> buffer = stackalloc byte[maxByteCount];
            var bytesWritten = Encoding.UTF8.GetBytes(text, buffer);
            hmac.AppendData(buffer[..bytesWritten]);
        }
        else
        {
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(maxByteCount);
            try
            {
                var bytesWritten = Encoding.UTF8.GetBytes(text, rentedBuffer);
                hmac.AppendData(rentedBuffer.AsSpan(0, bytesWritten));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        hmac.AppendData(Separator);
    }

    private static void AppendLongToHash(long value, IncrementalHash hmac)
    {
        Span<byte> buffer = stackalloc byte[20];
        if (Utf8Formatter.TryFormat(value, buffer, out var bytesWritten))
        {
            hmac.AppendData(buffer[..bytesWritten]);
        }

        hmac.AppendData(Separator);
    }

    private static void AppendGuidToHash(Guid value, IncrementalHash hmac)
    {
        Span<byte> buffer = stackalloc byte[36];
        if (Utf8Formatter.TryFormat(value, buffer, out var bytesWritten))
        {
            hmac.AppendData(buffer[..bytesWritten]);
        }

        hmac.AppendData(Separator);
    }

    private static void AppendDateToHash(DateTimeOffset value, IncrementalHash hmac)
    {
        Span<byte> buffer = stackalloc byte[33];
        if (Utf8Formatter.TryFormat(value, buffer, out var bytesWritten, 'O'))
        {
            hmac.AppendData(buffer[..bytesWritten]);
        }

        hmac.AppendData(Separator);
    }
}

/// <summary>Why a chain failed verification.</summary>
public enum AuditChainFailure
{
    /// <summary>A row's PreviousHash is not the hash of the row before it, or a chain index is missing: a row was deleted, inserted, reordered, or the chain forked.</summary>
    BrokenLink = 1,

    /// <summary>A row's stored hash does not match its content: the row was modified.</summary>
    HashMismatch = 2,

    /// <summary>The key of the row's segment is not in the key ring.</summary>
    UnknownKey = 3,
}

/// <summary>The outcome of walking (part of) a chain.</summary>
/// <param name="IsValid">False when a broken link, a hash mismatch or an unknown key was found.</param>
/// <param name="VerifiedCount">Rows whose link and hash were confirmed.</param>
/// <param name="PendingCount">Rows not hashed yet (normal at the tail).</param>
/// <param name="FailedAtChainIndex">The chain index of the first bad row (the missing index for a gap), when the chain is not valid.</param>
/// <param name="FailedAtSequence">The sequence number of the first bad row; null for a gap.</param>
/// <param name="Failure">What is wrong with that row.</param>
/// <param name="NextAfterChainIndex">When the walk stopped early, the chain index to resume after; otherwise null.</param>
public sealed record AuditChainVerification(
    bool IsValid,
    long VerifiedCount,
    long PendingCount,
    long? FailedAtChainIndex,
    long? FailedAtSequence,
    AuditChainFailure? Failure,
    long? NextAfterChainIndex = null);
