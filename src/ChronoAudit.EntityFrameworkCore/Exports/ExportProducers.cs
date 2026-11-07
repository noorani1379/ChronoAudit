using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>One batch of export rows.</summary>
/// <param name="Rows">The rows, one list of cells per row (the same number of cells as the producer's columns).</param>
/// <param name="NextCursor">Where the next batch continues; opaque to the worker. Null when <paramref name="Done"/> is true.</param>
/// <param name="Done">True when this was the last batch.</param>
public sealed record ExportBatchRows(IReadOnlyList<IReadOnlyList<SheetCell>> Rows, string? NextCursor, bool Done);

/// <summary>
/// Produces the rows of one kind of export. A producer is resolved from a new service scope for every call, so it may take scoped
/// services. It must page by a unique key (an id or a sequence), so rows with equal timestamps keep a stable order across batches.
/// A host adds its own with <c>AddExportProducer</c>.
/// </summary>
public interface IExportProducer
{
    /// <summary>The producer key: 1 to 64 characters of <c>a-z</c>, <c>0-9</c> and <c>-</c>.</summary>
    string Key { get; }

    /// <summary>True when only a caller with the All scope may ask for this export.</summary>
    bool RequiresAllScope { get; }

    /// <summary>Checks the parameters at request time; when it fails nothing is queued.</summary>
    /// <param name="parameters">The producer parameters.</param>
    /// <param name="scope">The requester's scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Success, or the reason the export cannot run.</returns>
    Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>The columns of the file (the header row).</summary>
    /// <param name="parameters">The producer parameters.</param>
    /// <param name="scope">The requester's scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The columns.</returns>
    Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Reads the next batch.</summary>
    /// <param name="parameters">The producer parameters.</param>
    /// <param name="scope">The requester's scope, as it was at request time.</param>
    /// <param name="cursor">The cursor of the previous batch; null for the first.</param>
    /// <param name="take">The most rows wanted; a producer may return fewer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The batch, or a predictable failure that fails the job.</returns>
    Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken);
}

internal static class ExportJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static ChronoAuditResult<T> Read<T>(JsonElement parameters)
        where T : class
    {
        try
        {
            return parameters.Deserialize<T>(Options)
                ?? (ChronoAuditResult<T>)ChronoAuditError.Validation("ChronoAudit.Validation", "The export parameters are missing.");
        }
        catch (JsonException)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The export parameters are not valid for this export.");
        }
    }

    public static SheetCell Cell(string? text) => SheetCell.Of(text);

    public static SheetCell Cell(decimal? number) => new(SheetCellKind.Number, null, number);

    public static SheetCell Cell(long number) => SheetCell.Of(number);

    public static SheetCell Instant(DateTimeOffset value) => SheetCell.Of(CsvWriter.Instant(value));

    public static ChronoAuditError RangeError(DateTimeOffset from, DateTimeOffset to)
        => to <= from
            ? ChronoAuditError.Validation("ChronoAudit.Validation", "The end of the range must be later than its start.")
            : ChronoAuditError.Validation("ChronoAudit.Validation", "The range may be at most 366 days.");

    public static bool RangeIsValid(DateTimeOffset from, DateTimeOffset to) => to > from && to - from <= TimeSpan.FromDays(366);

    public static ChronoAuditResult Ok => ChronoAuditResult.Success();
}

/// <summary>The parameters of the <c>analytics</c> export: a definition, or a saved report and an optional new period.</summary>
/// <param name="Definition">The analytics query to run.</param>
/// <param name="SavedReportId">A saved report the requester can see.</param>
/// <param name="From">A new start of the period for a saved report.</param>
/// <param name="To">A new end of the period for a saved report.</param>
public sealed record AnalyticsExportParameters(AnalyticsQuery? Definition = null, Guid? SavedReportId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

/// <summary>The analytics result as one table (it is bounded by top-N, so it is a single batch).</summary>
internal sealed class AnalyticsExportProducer(IAnalyticsService analytics, IAnalyticsPlanner planner, IServiceProvider services) : IExportProducer
{
    public string Key => "analytics";

    public bool RequiresAllScope => false;

    public async Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var definition = await ResolveAsync(parameters, scope, cancellationToken);
        return definition.IsFailure ? ChronoAuditResult.Failure(definition.Error) : planner.Validate(definition.Value, scope);
    }

    public async Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var definition = await ResolveAsync(parameters, scope, cancellationToken);
        if (definition.IsFailure)
        {
            throw new InvalidOperationException(definition.Error.Message);
        }

        var query = definition.Value;
        var entry = analytics.Catalog(scope).FirstOrDefault(c => c.Key == query.Subject);
        var columns = new List<SheetColumn>();
        columns.AddRange(query.Dimensions.Select(d => new SheetColumn(entry?.Dimensions.FirstOrDefault(x => x.Key == d)?.Title ?? d, 24)));
        columns.AddRange(query.Measures.Select(m => new SheetColumn(entry?.Measures.FirstOrDefault(x => x.Key == m)?.Title ?? m)));
        if (query.ComparePrevious)
        {
            columns.AddRange(query.Measures.Select(m => new SheetColumn((entry?.Measures.FirstOrDefault(x => x.Key == m)?.Title ?? m) + " (previous)")));
        }

        return columns;
    }

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        var definition = await ResolveAsync(parameters, scope, cancellationToken);
        if (definition.IsFailure)
        {
            return definition.Error;
        }

        var result = await analytics.RunAsync(definition.Value, scope, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var rows = new List<IReadOnlyList<SheetCell>>(result.Value.Rows.Count);
        foreach (var row in result.Value.Rows)
        {
            var cells = new List<SheetCell>();
            cells.AddRange(row.Dimensions.Select(ExportJson.Cell));
            cells.AddRange(row.Measures.Select(ExportJson.Cell));
            if (definition.Value.ComparePrevious)
            {
                cells.AddRange((row.Previous ?? row.Measures.Select(_ => (decimal?)null).ToList()).Select(ExportJson.Cell));
            }

            rows.Add(cells);
        }

        return new ExportBatchRows(rows, null, true);
    }

    private async Task<ChronoAuditResult<AnalyticsQuery>> ResolveAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var read = ExportJson.Read<AnalyticsExportParameters>(parameters);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var p = read.Value;
        if (p.Definition is not null)
        {
            return p.Definition;
        }

        if (p.SavedReportId is not { } id)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "An analytics export needs a definition or a saved report id.");
        }

        var saved = services.GetService<ISavedReportService>();
        if (saved is null)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "Saved reports are not enabled.");
        }

        // The report is read with the requester's scope: one the requester cannot see is "not found", never exported.
        var report = await saved.GetAsync(id, scope, cancellationToken);
        if (report.IsFailure)
        {
            return report.Error;
        }

        if (report.Value.Definition is not { } definition)
        {
            return ChronoAuditError.Unprocessable("SavedReport.Outdated", "The stored report definition can no longer be read; save it again.");
        }

        return definition with { From = p.From ?? definition.From, To = p.To ?? definition.To };
    }
}

/// <summary>The history extraction as a resumable export: the audit rows of the chosen modules by sequence, masked like the extraction.</summary>
internal sealed class ExtractExportProducer(IExtractReader reader) : IExportProducer
{
    public string Key => "extract";

    public bool RequiresAllScope => true;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var query = ExportJson.Read<ExtractQuery>(parameters);
        if (query.IsFailure)
        {
            return Task.FromResult(ChronoAuditResult.Failure(query.Error));
        }

        return Task.FromResult(ExportJson.RangeIsValid(query.Value.From, query.Value.To)
            ? ExportJson.Ok
            : ChronoAuditResult.Failure(ExportJson.RangeError(query.Value.From, query.Value.To)));
    }

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>(reader.Columns.Select(c => new SheetColumn(c.Header)).ToList());

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        var query = ExportJson.Read<ExtractQuery>(parameters);
        if (query.IsFailure)
        {
            return query.Error;
        }

        var batch = await reader.NextAsync(query.Value with { Cursor = cursor }, take, cancellationToken);
        if (batch.IsFailure)
        {
            return batch.Error;
        }

        var rows = batch.Value.Rows
            .Select(r => (IReadOnlyList<SheetCell>)reader.Columns.Select(c => Cell(c, r)).ToList())
            .ToList();
        return new ExportBatchRows(rows, batch.Value.Done ? null : batch.Value.NextCursor, batch.Value.Done);
    }

    // A column that is not text and reads as a whole number is a number cell; everything else stays text.
    private static SheetCell Cell(ExtractColumn column, ExtractRow row)
    {
        var text = column.Value(row);
        return !column.IsText && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? SheetCell.Of(number) : SheetCell.Of(text);
    }
}

/// <summary>The audit rows of one module over up to 366 days, newest first, keyset by sequence.</summary>
internal sealed class AuditExportProducer(ChronoAuditRegistry registry, IServiceProvider services) : IExportProducer
{
    private static readonly SheetColumn[] AllColumns =
    [
        new("Sequence"), new("OccurredAtUtc", 28), new("ActorUserId"), new("Action", 24), new("ResourceType"), new("ResourceId"),
        new("OldValues", 40), new("NewValues", 40), new("Metadata", 40), new("PayloadHash", 40),
    ];

    public string Key => "audit";

    public bool RequiresAllScope => true;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var query = ExportJson.Read<AuditSearchQuery>(parameters);
        if (query.IsFailure)
        {
            return Task.FromResult(ChronoAuditResult.Failure(query.Error));
        }

        if (!ExportJson.RangeIsValid(query.Value.From, query.Value.To))
        {
            return Task.FromResult(ChronoAuditResult.Failure(ExportJson.RangeError(query.Value.From, query.Value.To)));
        }

        return Task.FromResult(Find(query.Value.Module) is null
            ? ChronoAuditResult.Failure(ChronoAuditError.NotFound("Table.NotFound", "No audited table has this name."))
            : ExportJson.Ok);
    }

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>(AllColumns);

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        var read = ExportJson.Read<AuditSearchQuery>(parameters);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var query = read.Value;
        var contextType = Find(query.Module);
        if (contextType is null)
        {
            return ChronoAuditError.NotFound("Table.NotFound", "No audited table has this name.");
        }

        var db = (AuditableDbContext)services.GetRequiredService(contextType);
        var rows = db.AuditLogEntries.AsNoTracking().Where(a => a.OccurredAtUtc >= query.From && a.OccurredAtUtc <= query.To);
        if (!string.IsNullOrWhiteSpace(query.ActorUserId)) { var v = query.ActorUserId; rows = rows.Where(a => a.ActorUserId == v); }
        if (!string.IsNullOrWhiteSpace(query.ResourceType)) { var v = query.ResourceType; rows = rows.Where(a => a.ResourceType == v); }
        if (!string.IsNullOrWhiteSpace(query.ResourceId)) { var v = query.ResourceId; rows = rows.Where(a => a.ResourceId == v); }
        if (!string.IsNullOrWhiteSpace(query.Action)) { var v = query.Action; rows = rows.Where(a => a.Action == v); }
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var before))
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }

            rows = rows.Where(a => a.SequenceNumber < before);
        }

        var page = await rows.OrderByDescending(a => a.SequenceNumber).Take(take + 1)
            .Select(a => new { a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.ResourceType, a.ResourceId, a.OldValues, a.NewValues, a.Metadata, a.PayloadHash })
            .ToListAsync(cancellationToken);
        var more = page.Count > take;
        var items = more ? page.Take(take).ToList() : page;
        var cells = items.Select(a => (IReadOnlyList<SheetCell>)new List<SheetCell>
        {
            SheetCell.Of(a.SequenceNumber), ExportJson.Instant(a.OccurredAtUtc), SheetCell.Of(a.ActorUserId), SheetCell.Of(a.Action), SheetCell.Of(a.ResourceType),
            SheetCell.Of(a.ResourceId), SheetCell.Of(a.OldValues), SheetCell.Of(a.NewValues), SheetCell.Of(a.Metadata), SheetCell.Of(a.PayloadHash),
        }).ToList();
        return new ExportBatchRows(cells, more ? items[^1].SequenceNumber.ToString(CultureInfo.InvariantCulture) : null, !more);
    }

    private Type? Find(string module)
    {
        var entry = registry.Modules.FirstOrDefault(m => string.Equals(m.Module, module, StringComparison.OrdinalIgnoreCase));
        return entry.ContextType;
    }
}

/// <summary>The API activity log, newest first, keyset by id. A caller without the All scope gets only their own calls.</summary>
internal sealed class ActivityExportProducer(IServiceProvider services) : IExportProducer
{
    private const int MaxTake = 200;

    private static readonly SheetColumn[] AllColumns =
    [
        new("Id"), new("RequestId", 38), new("OccurredAtUtc", 28), new("DurationMs"), new("UserId"), new("SessionId"), new("Method"), new("Path", 40),
        new("EndpointKey", 30), new("StatusCode"), new("ErrorCode", 24), new("IpAddress"),
    ];

    public string Key => "activity";

    public bool RequiresAllScope => false;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var search = services.GetService<IActivitySearch>();
        if (search is null)
        {
            return Task.FromResult(ChronoAuditResult.Failure(ChronoAuditError.Validation("Export.UnknownProducer", "The activity log is not enabled.")));
        }

        var query = Scoped(parameters, scope);
        if (query.IsFailure)
        {
            return Task.FromResult(ChronoAuditResult.Failure(query.Error));
        }

        var q = query.Value;
        var wide = q.UserId is not null || q.SessionId is not null || q.CorrelationId is not null;
        var maxDays = wide ? 366 : 31;
        return Task.FromResult(q.From <= q.To && q.To - q.From <= TimeSpan.FromDays(maxDays)
            ? ExportJson.Ok
            : ChronoAuditResult.Failure(ChronoAuditError.Validation(
                "ChronoAudit.Validation", $"The range must not be reversed and may be at most {maxDays} days{(wide ? string.Empty : " unless a user, a session or a correlation id is given")}.")));
    }

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>(AllColumns);

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        var search = services.GetRequiredService<IActivitySearch>();
        var query = Scoped(parameters, scope);
        if (query.IsFailure)
        {
            return query.Error;
        }

        var page = await search.SearchAsync(query.Value with { Before = cursor, Limit = Math.Clamp(take, 1, MaxTake) }, cancellationToken);
        if (page.IsFailure)
        {
            return page.Error;
        }

        var cells = page.Value.Items.Select(r => (IReadOnlyList<SheetCell>)new List<SheetCell>
        {
            SheetCell.Of(r.Id), SheetCell.Of(r.RequestId.ToString("D")), ExportJson.Instant(r.OccurredAtUtc), SheetCell.Of(r.DurationMs), SheetCell.Of(r.UserId),
            SheetCell.Of(r.SessionId), SheetCell.Of(r.HttpMethod), SheetCell.Of(r.Path), SheetCell.Of(r.EndpointKey), SheetCell.Of(r.StatusCode),
            SheetCell.Of(r.ErrorCode), SheetCell.Of(r.IpAddress),
        }).ToList();
        return new ExportBatchRows(cells, page.Value.NextCursor, page.Value.NextCursor is null);
    }

    // Without the All scope the query is pinned to the requester's own calls; asking for another user is refused, never silently widened.
    private static ChronoAuditResult<ActivitySearchQuery> Scoped(JsonElement parameters, ChronoAuditScope scope)
    {
        var read = ExportJson.Read<ActivitySearchQuery>(parameters);
        if (read.IsFailure || scope.Kind == ScopeKind.All)
        {
            return read;
        }

        if (scope.UserId is null)
        {
            return ChronoAuditError.Forbidden("Export.KindForbidden", "This export needs a signed-in user.");
        }

        if (read.Value.UserId is not null && !string.Equals(read.Value.UserId, scope.UserId, StringComparison.Ordinal))
        {
            return ChronoAuditError.Forbidden("Export.KindForbidden", "You can only export your own activity.");
        }

        return read.Value with { UserId = scope.UserId };
    }
}

/// <summary>The parameters of the <c>timeline</c> export.</summary>
/// <param name="From">The start of the period (required).</param>
/// <param name="To">The end of the period (required; at most 366 days after the start).</param>
/// <param name="SubjectType">Only this subject type.</param>
/// <param name="SubjectId">Only this subject (needs a subject type).</param>
/// <param name="GroupKey">Only this group key.</param>
public sealed record TimelineExportParameters(DateTimeOffset From, DateTimeOffset To, string? SubjectType = null, string? SubjectId = null, string? GroupKey = null);

/// <summary>The business events of a period, newest first, paged by time and id, inside the requester's scope, with the payload masked as when read.</summary>
internal sealed class TimelineExportProducer(
    ChronoAuditRegistry registry, ChronoAuditStoreRegistration registration, IServiceProvider services, SensitiveDataMasker masker, PersonalDataMasking personalData) : IExportProducer
{
    private static readonly SheetColumn[] AllColumns =
    [
        new("OccurredAtUtc", 28), new("EventType", 24), new("Title", 30), new("SubjectType"), new("SubjectId"), new("GroupKey"),
        new("ActorDisplayName", 24), new("ActorRole"), new("Payload", 50),
    ];

    public string Key => "timeline";

    public bool RequiresAllScope => false;

    public Task<ChronoAuditResult> ValidateAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        if (!registry.TimelineAdded)
        {
            return Task.FromResult(ChronoAuditResult.Failure(ChronoAuditError.Validation("Export.UnknownProducer", "The timeline is not enabled.")));
        }

        var read = ExportJson.Read<TimelineExportParameters>(parameters);
        if (read.IsFailure)
        {
            return Task.FromResult(ChronoAuditResult.Failure(read.Error));
        }

        var p = read.Value;
        if (!ExportJson.RangeIsValid(p.From, p.To))
        {
            return Task.FromResult(ChronoAuditResult.Failure(ExportJson.RangeError(p.From, p.To)));
        }

        return Task.FromResult(p.SubjectId is not null && p.SubjectType is null
            ? ChronoAuditResult.Failure(ChronoAuditError.Validation("ChronoAudit.Validation", "A subject id needs a subject type."))
            : ExportJson.Ok);
    }

    public Task<IReadOnlyList<SheetColumn>> ColumnsAsync(JsonElement parameters, ChronoAuditScope scope, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SheetColumn>>(AllColumns);

    public async Task<ChronoAuditResult<ExportBatchRows>> NextAsync(JsonElement parameters, ChronoAuditScope scope, string? cursor, int take, CancellationToken cancellationToken)
    {
        var read = ExportJson.Read<TimelineExportParameters>(parameters);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var p = read.Value;
        var db = (DbContext)services.GetRequiredService(registration.ContextType);
        var from = p.From.ToUniversalTime();
        var to = p.To.ToUniversalTime();
        var events = TimelineScope.Visible(db, db.Set<TimelineEvent>().AsNoTracking(), scope).Where(e => e.OccurredAtUtc >= from && e.OccurredAtUtc <= to);
        if (p.SubjectType is { } type) { events = events.Where(e => e.SubjectType == type); }
        if (p.SubjectId is { } id) { events = events.Where(e => e.SubjectId == id); }
        if (p.GroupKey is { } group) { events = events.Where(e => e.GroupKey == group); }
        if (cursor is not null)
        {
            var position = TimelineCursor.Decode(cursor);
            if (position is null)
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }

            var at = new DateTimeOffset(position.OccurredAtTicks, TimeSpan.Zero);
            var last = position.Id;
            events = events.Where(e => e.OccurredAtUtc < at || (e.OccurredAtUtc == at && e.Id < last));
        }

        var page = await events.OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id).Take(take + 1)
            .Select(e => new { e.Id, e.EventType, e.SchemaVersion, e.SubjectType, e.SubjectId, e.GroupKey, e.OccurredAtUtc, e.ActorDisplayName, e.ActorRole, e.Payload })
            .ToListAsync(cancellationToken);
        var more = page.Count > take;
        var items = more ? page.Take(take).ToList() : page;
        var titles = services.GetService<ITimelineTitleProvider>();
        var rows = items.Select(e => (IReadOnlyList<SheetCell>)new List<SheetCell>
        {
            ExportJson.Instant(e.OccurredAtUtc), SheetCell.Of(e.EventType), SheetCell.Of(titles?.TitleOf(e.EventType, e.SchemaVersion, e.Payload) ?? e.EventType),
            SheetCell.Of(e.SubjectType), SheetCell.Of(e.SubjectId), SheetCell.Of(e.GroupKey), SheetCell.Of(e.ActorDisplayName), SheetCell.Of(e.ActorRole),
            SheetCell.Of(personalData.MaskJson(masker.MaskJson(e.Payload, codeIsSecret: false), reveal: false)),
        }).ToList();
        var next = more ? new TimelineCursor(items[^1].OccurredAtUtc.UtcTicks, items[^1].Id).Encode() : null;
        return new ExportBatchRows(rows, next, !more);
    }
}
