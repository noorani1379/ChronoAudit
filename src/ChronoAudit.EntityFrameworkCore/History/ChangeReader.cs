using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>One field that changed: its name and the value before and after (null when it did not exist on that side).</summary>
/// <param name="Field">The field (column) name.</param>
/// <param name="From">The value before; null for an insert.</param>
/// <param name="To">The value after; null for a delete.</param>
public sealed record FieldChange(string Field, JsonElement? From, JsonElement? To);

/// <summary>One audit row as a list of field changes.</summary>
/// <param name="Sequence">The sequence number of the audit row.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="Action">The action as stored (<c>Auto_Added</c>, <c>Auto_Modified</c>, ... or a host name).</param>
/// <param name="ActorUserId">Who did it.</param>
/// <param name="ActorDisplayName">The actor's name, when the host wrote it into the row metadata (<c>ActorDisplayName</c>).</param>
/// <param name="OnBehalfOfUserId">The user the action was done for, when the host wrote it into the row metadata (<c>OnBehalfOfUserId</c>).</param>
/// <param name="ResourceType">The table.</param>
/// <param name="RecordKey">The key of the record as the audit wrote it (JSON).</param>
/// <param name="Fields">The changed fields.</param>
/// <param name="PayloadHash">The hash of the row.</param>
public sealed record ChangeRow(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string Action,
    string ActorUserId,
    string? ActorDisplayName,
    string? OnBehalfOfUserId,
    string ResourceType,
    string RecordKey,
    IReadOnlyList<FieldChange> Fields,
    string? PayloadHash);

/// <summary>One page of changes.</summary>
/// <param name="Items">The rows, oldest first.</param>
/// <param name="NextCursor">The cursor of the next page; null on the last page.</param>
/// <param name="PersonalDataRevealed">True when personal fields in this page are whole (asked for, allowed and audited).</param>
/// <param name="AsOfUtc">The time the page was read.</param>
public sealed record ChangePage(IReadOnlyList<ChangeRow> Items, string? NextCursor, bool PersonalDataRevealed, DateTimeOffset AsOfUtc);

/// <summary>Reads the field-level history of a table or of one record; personal fields are masked unless a reveal was asked for, allowed and audited first.</summary>
public interface IChangeReader
{
    /// <summary>The changes of a whole table, oldest first. Only for the <see cref="ScopeKind.All"/> scope.</summary>
    /// <param name="module">The module name.</param>
    /// <param name="table">The table.</param>
    /// <param name="scope">The caller's scope.</param>
    /// <param name="from">The start of the range, if any.</param>
    /// <param name="to">The end of the range, if any.</param>
    /// <param name="cursor">The cursor of the previous page.</param>
    /// <param name="limit">The page size, 1 to 200.</param>
    /// <param name="reveal">True to ask for personal data unmasked.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page, or <c>Table.NotFound</c>, <c>ChronoAudit.InvalidCursor</c>, <c>ChronoAudit.Validation</c>.</returns>
    Task<ChronoAuditResult<ChangePage>> TableAsync(
        string module, string table, ChronoAuditScope scope, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, bool reveal, CancellationToken cancellationToken);

    /// <summary>The changes of one record, oldest first. Only for the <see cref="ScopeKind.All"/> scope.</summary>
    /// <param name="module">The module name.</param>
    /// <param name="table">The table.</param>
    /// <param name="recordKey">The key of the record (parts joined with <c>|</c> for a composite key).</param>
    /// <param name="scope">The caller's scope.</param>
    /// <param name="cursor">The cursor of the previous page.</param>
    /// <param name="limit">The page size, 1 to 200.</param>
    /// <param name="reveal">True to ask for personal data unmasked.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page, or <c>Table.NotFound</c>, <c>Record.NotFound</c>, <c>Table.RecordLookupNotSupported</c>, <c>ChronoAudit.InvalidCursor</c>, <c>ChronoAudit.Validation</c>.</returns>
    Task<ChronoAuditResult<ChangePage>> RecordAsync(
        string module, string table, string recordKey, ChronoAuditScope scope, string? cursor, int limit, bool reveal, CancellationToken cancellationToken);
}

internal sealed class ChangeReader(
    IAuditHistory history,
    PersonalDataMasking personalData,
    IPersonalDataRevealPolicy revealPolicy,
    ChronoAuditRegistry registry,
    IServiceProvider services,
    TimeProvider clock,
    ILogger<ChangeReader> logger) : IChangeReader
{
    private const string CursorPrefix = "c1:";

    private static readonly ChronoAuditError TableNotFound = ChronoAuditError.NotFound("Table.NotFound", "No audited table has this name.");

    public async Task<ChronoAuditResult<ChangePage>> TableAsync(
        string module, string table, ChronoAuditScope scope, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, bool reveal, CancellationToken cancellationToken)
    {
        var resolved = Resolve(module, table, scope);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var invalid = ValidatePaging(cursor, limit, out var after) ?? (from is { } f && to is { } t && t <= f
            ? ChronoAuditError.Validation("ChronoAudit.Validation", "The end of the range must be later than its start.")
            : null);
        if (invalid is not null)
        {
            return invalid;
        }

        var (source, info) = resolved.Value;
        var rows = await source.ListTableChangesAsync(info.Table, from, to, after, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var parsed = page.Select(r => Parse(r.Row, r.ResourceType, RecordKeyOf(r.ResourceId))).ToList();
        return await BuildPageAsync(module, info.Table, "*", scope, parsed, hasMore, page.Count == 0 ? null : page[^1].Row.Sequence, reveal, cancellationToken);
    }

    public async Task<ChronoAuditResult<ChangePage>> RecordAsync(
        string module, string table, string recordKey, ChronoAuditScope scope, string? cursor, int limit, bool reveal, CancellationToken cancellationToken)
    {
        var resolved = Resolve(module, table, scope);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var invalid = ValidatePaging(cursor, limit, out var after);
        if (invalid is not null)
        {
            return invalid;
        }

        var (source, info) = resolved.Value;
        if (!KeyFits(info, recordKey))
        {
            return ChronoAuditError.Unprocessable("Table.RecordLookupNotSupported", "The record key does not match the key of this table.");
        }

        var rows = await source.ListRecordChangesAsync(info.Table, recordKey, after == 0 ? null : after, limit + 1, cancellationToken);

        // A record with no history at all looks exactly like a record that does not exist; an empty later page is just the end.
        if (rows.Count == 0 && cursor is null)
        {
            return ChronoAuditError.NotFound("Record.NotFound", "This record has no history.");
        }

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var parsed = page.Select(r => Parse(r, info.Table, recordKey)).ToList();
        return await BuildPageAsync(module, info.Table, recordKey, scope, parsed, hasMore, page.Count == 0 ? null : page[^1].Sequence, reveal, cancellationToken);
    }

    private static ChronoAuditError? ValidatePaging(string? cursor, int limit, out long after)
    {
        after = 0;
        if (limit < 1 || limit > IModuleAuditHistorySource.MaxPageSize)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"Limit must be between 1 and {IModuleAuditHistorySource.MaxPageSize}.");
        }

        if (cursor is not null)
        {
            if (!CursorCodec.TryDecode(cursor, CursorPrefix, out var text)
                || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out after))
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }
        }

        return null;
    }

    // Only the All scope may read table history; everyone else gets exactly the answer for a table that does not exist, so names cannot be probed.
    private ChronoAuditResult<(IModuleAuditHistorySource Source, AuditedTable Info)> Resolve(string module, string table, ChronoAuditScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Kind != ScopeKind.All)
        {
            return TableNotFound;
        }

        var source = history.Find(module ?? string.Empty);
        var info = source?.Tables.FirstOrDefault(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));
        return source is null || info is null ? TableNotFound : (source, info);
    }

    private static bool KeyFits(AuditedTable info, string recordKey)
    {
        if (!info.RecordLookup || string.IsNullOrEmpty(recordKey))
        {
            return false;
        }

        var parts = info.Key.Count == 1 ? [recordKey] : recordKey.Split('|');
        if (parts.Length != info.Key.Count)
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var fits = info.Key[i].Type switch
            {
                "guid" => Guid.TryParse(parts[i], out _),
                "int" => int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "long" => long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "short" => short.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "string" => true,
                _ => false,
            };
            if (!fits)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ChronoAuditResult<ChangePage>> BuildPageAsync(
        string module, string table, string resourceId, ChronoAuditScope scope, List<ChangeRow> rows, bool hasMore, long? lastSequence, bool reveal, CancellationToken cancellationToken)
    {
        var personal = rows.SelectMany(r => r.Fields).Select(f => f.Field).Where(personalData.IsPersonal).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Reveal only on request, only when personal fields are present, only when allowed and only after the audit row exists.
        var revealed = reveal && personal.Count > 0
                       && await revealPolicy.MayRevealAsync(scope, table, cancellationToken)
                       && await RecordRevealAsync(module, table, resourceId, scope, personal, cancellationToken);

        var items = revealed
            ? rows
            : rows.Select(r => r with { Fields = r.Fields.Select(f => personalData.IsPersonal(f.Field) ? f with { From = personalData.Mask(f.Field, f.From), To = personalData.Mask(f.Field, f.To) } : f).ToList() }).ToList();

        var next = hasMore && lastSequence is { } last ? CursorCodec.Encode(CursorPrefix, last.ToString(CultureInfo.InvariantCulture)) : null;
        return new ChangePage(items, next, revealed, clock.GetUtcNow());
    }

    // The audit row is saved BEFORE the data is returned: no row, no reveal. A failure never raises to the caller (the page stays masked); cancellation does.
    private async Task<bool> RecordRevealAsync(
        string module, string table, string resourceId, ChronoAuditScope scope, IReadOnlyList<string> fields, CancellationToken cancellationToken)
    {
        var accessor = services.GetRequiredService<IAuditLogAccessor>();
        try
        {
            var entry = registry.Modules.First(m => string.Equals(m.Module, module, StringComparison.OrdinalIgnoreCase));
            var db = (AuditableDbContext)services.GetRequiredService(entry.ContextType);
            var viewer = scope.UserId ?? "System";
            await services.GetRequiredService<IAuditLogWriter>().AppendHashedAsync(
                viewer, AuditActions.PersonalDataRevealed, table, resourceId, new { viewer = scope.UserId, fields }, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            accessor.Clear();
            throw;
        }
        catch (Exception ex)
        {
            accessor.Clear();
            logger.LogWarning(ex, "The audit row of a personal data reveal on {Table} could not be saved; the page is returned masked.", table);
            return false;
        }
    }

    // The audit writes the key as JSON ({"OrderId":"..","LineNo":2}); readers use the same text a caller passes in: the parts joined with '|'.
    private static string RecordKeyOf(string resourceId)
    {
        try
        {
            using var document = JsonDocument.Parse(resourceId);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? string.Join('|', document.RootElement.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()))
                : resourceId;
        }
        catch (JsonException)
        {
            return resourceId;
        }
    }

    private static ChangeRow Parse(AuditHistoryRow row, string resourceType, string resourceId)
    {
        var old = Fields(row.OldValuesJson);
        var current = Fields(row.NewValuesJson);
        var fields = new List<FieldChange>();
        foreach (var name in current.Keys.Concat(old.Keys.Where(k => !current.ContainsKey(k))))
        {
            old.TryGetValue(name, out var from);
            current.TryGetValue(name, out var to);
            if (from.ValueKind != JsonValueKind.Undefined && from.ValueKind == to.ValueKind && from.GetRawText() == to.GetRawText())
            {
                continue;
            }

            fields.Add(new FieldChange(
                name, from.ValueKind == JsonValueKind.Undefined ? null : from, to.ValueKind == JsonValueKind.Undefined ? null : to));
        }

        var (displayName, onBehalfOf) = ReadMetadata(row.MetadataJson);
        return new ChangeRow(
            row.Sequence, row.OccurredAtUtc, row.Action, row.ActorUserId, displayName, onBehalfOf, resourceType, resourceId, fields, row.PayloadHash);
    }

    // Insertion order is kept (a dictionary of JsonElements read from the object in document order).
    private static Dictionary<string, JsonElement> Fields(string? json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    result[property.Name] = property.Value.Clone();
                }
            }
        }
        catch (JsonException)
        {
            // A damaged row has no readable fields; the hash chain is what reports damage.
        }

        return result;
    }

    private static (string? DisplayName, string? OnBehalfOf) ReadMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (Read(root, "ActorDisplayName"), Read(root, "OnBehalfOfUserId"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? Read(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
