using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The result of verifying one module's hash chain over the rows of an evidence bundle.</summary>
/// <param name="Module">The module name.</param>
/// <param name="Verification">The verification of the chain-index range of the rows found in that module.</param>
public sealed record EvidenceChainCheck(string Module, AuditChainVerification Verification);

/// <summary>Everything ChronoAudit knows about one subject, in one object a host can render with any tool.</summary>
/// <param name="SubjectType">The subject type.</param>
/// <param name="SubjectId">The subject identity.</param>
/// <param name="DataSha256">The SHA-256 (upper-case hex) over the timeline events, oldest first; it changes when any event or their order changes.</param>
/// <param name="Timeline">The events of the subject, newest first (payloads masked).</param>
/// <param name="AuditRows">The audit rows of the subject from every module (secrets and personal fields masked), by module then sequence.</param>
/// <param name="ChainChecks">The verification of each module hash chain over the range of the rows found there.</param>
/// <param name="GeneratedAtUtc">When the bundle was issued.</param>
public sealed record EvidenceBundle(
    string SubjectType, string SubjectId, string DataSha256, IReadOnlyList<TimelineItem> Timeline, IReadOnlyList<AuditSearchRow> AuditRows,
    IReadOnlyList<EvidenceChainCheck> ChainChecks, DateTimeOffset GeneratedAtUtc);

/// <summary>Issues evidence bundles. Only the All scope may ask; every issue is recorded in the audit table first.</summary>
public interface IEvidenceService
{
    /// <summary>The most timeline events or audit rows a bundle holds.</summary>
    public const int MaxItems = 1000;

    /// <summary>Builds the bundle of a subject.</summary>
    /// <param name="subjectType">The subject type (also the audit resource type).</param>
    /// <param name="subjectId">The subject identity.</param>
    /// <param name="scope">The data scope of the caller; anything but All gets <c>Evidence.NotFound</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The bundle, or <c>Evidence.NotFound</c>, <c>Evidence.TooLarge</c>, <c>ChronoAudit.AuditUnavailable</c>.</returns>
    Task<ChronoAuditResult<EvidenceBundle>> BuildAsync(string subjectType, string subjectId, ChronoAuditScope scope, CancellationToken cancellationToken);
}

/// <summary>The fingerprint of a story: the events in order, the fields joined by a separator that cannot occur in the data.</summary>
internal static class EvidenceHash
{
    public static string Compute(IEnumerable<TimelineItem> eventsOldestFirst)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var e in eventsOldestFirst)
        {
            var line = string.Join(
                '\u001f',
                e.Id.ToString(CultureInfo.InvariantCulture),
                e.SourceEventId.ToString("D"),
                e.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                e.EventType,
                e.SubjectType,
                e.SubjectId,
                e.GroupKey ?? string.Empty,
                e.ActorDisplayName,
                e.ActorRole ?? string.Empty,
                e.OnBehalfOfUserId ?? string.Empty,
                e.Payload) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal sealed class EvidenceService(
    ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory, ITimelineReader timeline, AuditChain chain, SensitiveDataMasker masker, PersonalDataMasking personalData,
    TimeProvider clock) : IEvidenceService
{
    private static readonly ChronoAuditError NotFound = ChronoAuditError.NotFound("Evidence.NotFound", "No evidence was found for this subject.");
    private static readonly ChronoAuditError TooLarge = ChronoAuditError.Validation(
        "Evidence.TooLarge", $"The subject has more than {IEvidenceService.MaxItems} events or audit rows; use the full extract (CSV) instead.");

    public async Task<ChronoAuditResult<EvidenceBundle>> BuildAsync(string subjectType, string subjectId, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Kind != ScopeKind.All || string.IsNullOrWhiteSpace(subjectType) || string.IsNullOrWhiteSpace(subjectId))
        {
            return NotFound;
        }

        // The story: every event of the subject, newest first as the reader returns them.
        var events = new List<TimelineItem>();
        string? cursor = null;
        do
        {
            var page = await timeline.OfSubjectAsync(subjectType, subjectId, new TimelineFilter(Before: cursor, Limit: 200), scope, cancellationToken);
            if (page.IsFailure)
            {
                return page.Error;
            }

            events.AddRange(page.Value.Items);
            if (events.Count > IEvidenceService.MaxItems)
            {
                return TooLarge;
            }

            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        var rows = new List<AuditSearchRow>();
        var checks = new List<EvidenceChainCheck>();
        var keys = new List<string> { subjectId };
        if (Guid.TryParse(subjectId, out var guid))
        {
            keys.Add(AuditResourceKey.Of(guid));
        }

        using var scopeOfCall = scopeFactory.CreateScope();
        foreach (var (contextType, module) in registry.Modules.OrderBy(m => m.Module, StringComparer.Ordinal))
        {
            var db = (AuditableDbContext)scopeOfCall.ServiceProvider.GetRequiredService(contextType);

            // The audit stores the key as JSON of the key properties ({"Id":7}); the caller gives the key as text (7). The table's own key tells how.
            var moduleKeys = keys;
            var entity = db.Model.GetEntityTypes().FirstOrDefault(t => t.BaseType is null && !t.IsOwned()
                && string.Equals(t.GetTableName(), subjectType, StringComparison.OrdinalIgnoreCase));
            if (entity?.FindPrimaryKey() is { } primaryKey
                && AuditResourceKey.TryOf(primaryKey.Properties.Select(p => (p.Name, Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType)).ToList(), subjectId, out var storedKey))
            {
                moduleKeys = [.. keys, storedKey];
            }

            var found = await db.AuditLogEntries.AsNoTracking()
                .Where(a => a.ResourceType == subjectType && moduleKeys.Contains(a.ResourceId))
                .OrderBy(a => a.SequenceNumber)
                .Take(IEvidenceService.MaxItems + 1)
                .Select(a => new
                {
                    a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.ResourceType, a.ResourceId, a.OldValues, a.NewValues, a.Metadata, a.PreviousHash, a.PayloadHash, a.ChainIndex,
                })
                .ToListAsync(cancellationToken);
            if (found.Count == 0)
            {
                continue;
            }

            if (rows.Count + found.Count > IEvidenceService.MaxItems)
            {
                return TooLarge;
            }

            rows.AddRange(found.Select(a => new AuditSearchRow(
                a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.ResourceType, a.ResourceId, Mask(a.OldValues), Mask(a.NewValues), Mask(a.Metadata), a.PreviousHash, a.PayloadHash)));

            // The chain is verified over the index range of the rows found; rows that are not hashed yet are reported as pending.
            var indexes = found.Where(a => a.ChainIndex is not null).Select(a => a.ChainIndex!.Value).ToList();
            var pending = found.Count - indexes.Count;
            if (indexes.Count == 0)
            {
                checks.Add(new EvidenceChainCheck(module, new AuditChainVerification(true, 0, pending, null, null, null)));
                continue;
            }

            var verification = await chain.VerifyAsync(
                db, cancellationToken, afterChainIndex: indexes.Min() - 1, maxRows: checked((int)Math.Min(int.MaxValue, indexes.Max() - indexes.Min() + 1)), module: module);
            checks.Add(new EvidenceChainCheck(module, pending == 0 ? verification : verification with { PendingCount = verification.PendingCount + pending }));
        }

        if (events.Count == 0 && rows.Count == 0)
        {
            return NotFound;
        }

        var story = events.AsEnumerable().Reverse().ToList();
        var dataHash = EvidenceHash.Compute(story);

        // No record of the issue, no bundle.
        if (!await RecordIssueAsync(scopeOfCall.ServiceProvider, scope, subjectType, subjectId, dataHash, events.Count, rows.Count, cancellationToken))
        {
            return ChronoAuditError.Unavailable("ChronoAudit.AuditUnavailable", "The issue of the evidence could not be recorded, so nothing is returned; try again.");
        }

        return new EvidenceBundle(subjectType, subjectId, dataHash, events, rows, checks, clock.GetUtcNow());
    }

    private async Task<bool> RecordIssueAsync(
        IServiceProvider services, ChronoAuditScope scope, string subjectType, string subjectId, string dataHash, int eventCount, int auditRowCount, CancellationToken cancellationToken)
    {
        if (registry.Modules.Count == 0)
        {
            return false;
        }

        try
        {
            var audit = (AuditableDbContext)services.GetRequiredService(registry.Modules[0].ContextType);
            await services.GetRequiredService<IAuditLogWriter>().AppendHashedAsync(
                scope.UserId ?? "unknown", AuditActions.EvidenceIssued, subjectType, subjectId, new { Events = eventCount, AuditRows = auditRowCount, DataSha256 = dataHash }, cancellationToken);
            await audit.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            services.GetRequiredService<IAuditLogAccessor>().Clear();
            return false;
        }
    }

    private string? Mask(string? json) => json is null ? null : personalData.MaskJson(masker.MaskJson(json, codeIsSecret: false), reveal: false);
}
