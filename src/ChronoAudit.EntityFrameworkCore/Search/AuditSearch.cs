using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>A search over the audit rows of one module.</summary>
/// <param name="Module">The module name.</param>
/// <param name="From">The start of the range (inclusive).</param>
/// <param name="To">The end of the range (inclusive); later than <paramref name="From"/>.</param>
/// <param name="ActorUserId">Only rows by this actor.</param>
/// <param name="ResourceType">Only rows of this table.</param>
/// <param name="ResourceId">Only rows of this resource id (the JSON key as written by the audit).</param>
/// <param name="Action">Only rows with this action.</param>
/// <param name="Cursor">The cursor of the previous page; null for the first page.</param>
/// <param name="Limit">The page size, 1 to 200.</param>
public sealed record AuditSearchQuery(
    string Module,
    DateTimeOffset From,
    DateTimeOffset To,
    string? ActorUserId = null,
    string? ResourceType = null,
    string? ResourceId = null,
    string? Action = null,
    string? Cursor = null,
    int Limit = 50);

/// <summary>One audit row as the search returns it.</summary>
/// <param name="Sequence">The sequence number of the row.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="ActorUserId">Who did it.</param>
/// <param name="Action">What was done.</param>
/// <param name="ResourceType">The table.</param>
/// <param name="ResourceId">The key of the record.</param>
/// <param name="OldValues">The values before (JSON).</param>
/// <param name="NewValues">The values after (JSON).</param>
/// <param name="Metadata">Extra details (JSON).</param>
/// <param name="PreviousHash">The previous hash of the chain.</param>
/// <param name="PayloadHash">The hash of the row.</param>
public sealed record AuditSearchRow(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string ActorUserId,
    string Action,
    string ResourceType,
    string ResourceId,
    string? OldValues,
    string? NewValues,
    string? Metadata,
    string? PreviousHash,
    string? PayloadHash);

/// <summary>One page of a search, newest first.</summary>
/// <param name="Items">The rows.</param>
/// <param name="NextCursor">The cursor of the next page; null on the last page.</param>
public sealed record AuditSearchPage(IReadOnlyList<AuditSearchRow> Items, string? NextCursor);

/// <summary>Searches the audit rows of a module by actor, resource, action and time, newest first.</summary>
public interface IAuditSearch
{
    /// <summary>Runs one page of the search.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page, or <c>ChronoAudit.Validation</c>, <c>ChronoAudit.InvalidCursor</c> or <c>Table.NotFound</c> (unknown module).</returns>
    Task<ChronoAuditResult<AuditSearchPage>> SearchAsync(AuditSearchQuery query, CancellationToken cancellationToken);
}

/// <summary>The opaque cursors of the readers: a prefix and a number, as base64url text.</summary>
internal static class CursorCodec
{
    public static string Encode(string prefix, string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(prefix + value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? cursor, string prefix, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 512)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            value = text[prefix.Length..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class AuditSearchService(ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory) : IAuditSearch
{
    private const int MaxLimit = 200;
    private const string CursorPrefix = "a1:";

    public async Task<ChronoAuditResult<AuditSearchPage>> SearchAsync(AuditSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit < 1 || query.Limit > MaxLimit)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"Limit must be between 1 and {MaxLimit}.");
        }

        if (query.To <= query.From)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The end of the range must be later than its start.");
        }

        var narrowed = !string.IsNullOrWhiteSpace(query.ActorUserId) || !string.IsNullOrWhiteSpace(query.ResourceId);
        var maxDays = narrowed ? 366 : 31;
        if (query.To - query.From > TimeSpan.FromDays(maxDays))
        {
            return ChronoAuditError.Validation(
                "ChronoAudit.Validation", $"The range may be at most {maxDays} days{(narrowed ? string.Empty : " unless an actor or a resource is given")}.");
        }

        long? before = null;
        if (query.Cursor is not null)
        {
            if (!CursorCodec.TryDecode(query.Cursor, CursorPrefix, out var text) || !long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seq) || seq < 0)
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }

            before = seq;
        }

        var entry = registry.Modules.FirstOrDefault(m => string.Equals(m.Module, query.Module, StringComparison.OrdinalIgnoreCase));
        if (entry.ContextType is null)
        {
            return ChronoAuditError.NotFound("Table.NotFound", "No audited table has this name.");
        }

        using var scope = scopeFactory.CreateScope();
        var db = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(entry.ContextType);

        var rows = db.AuditLogEntries.AsNoTracking().Where(a => a.OccurredAtUtc >= query.From && a.OccurredAtUtc <= query.To);
        if (!string.IsNullOrWhiteSpace(query.ActorUserId))
        {
            var actor = query.ActorUserId;
            rows = rows.Where(a => a.ActorUserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.ResourceType))
        {
            var type = query.ResourceType;
            rows = rows.Where(a => a.ResourceType == type);
        }

        if (!string.IsNullOrWhiteSpace(query.ResourceId))
        {
            var id = query.ResourceId;
            rows = rows.Where(a => a.ResourceId == id);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action;
            rows = rows.Where(a => a.Action == action);
        }

        if (before is { } cursorSequence)
        {
            rows = rows.Where(a => a.SequenceNumber < cursorSequence);
        }

        var page = await rows.OrderByDescending(a => a.SequenceNumber).Take(query.Limit + 1)
            .Select(a => new AuditSearchRow(
                a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.ResourceType, a.ResourceId, a.OldValues, a.NewValues, a.Metadata, a.PreviousHash, a.PayloadHash))
            .ToListAsync(cancellationToken);

        var more = page.Count > query.Limit;
        var items = more ? page.Take(query.Limit).ToList() : page;
        return new AuditSearchPage(items, more ? CursorCodec.Encode(CursorPrefix, items[^1].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null);
    }
}
