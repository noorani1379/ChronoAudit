using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>A streaming extract of the audit rows of every module (or of chosen modules and tables).</summary>
/// <param name="From">The start of the range (inclusive).</param>
/// <param name="To">The end of the range (inclusive); later than <paramref name="From"/>, at most 366 days after it.</param>
/// <param name="Modules">Only these modules; null or empty means all.</param>
/// <param name="ResourceType">Only rows of this table.</param>
/// <param name="ActorUserId">Only rows by this actor.</param>
/// <param name="Cursor">Where to resume (the <see cref="ExtractBatch.NextCursor"/> of the last batch); null to start.</param>
/// <param name="Tables">Only these tables, as <c>module/table</c> items; the extract then reads only their modules. A malformed item is refused.</param>
/// <param name="IncludeTimeline">True to stream the timeline events first (module name <c>timeline</c>, sequence = event id), then the audit modules. Needs <c>AddTimeline</c>.</param>
public sealed record ExtractQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<string>? Modules = null,
    string? ResourceType = null,
    string? ActorUserId = null,
    string? Cursor = null,
    IReadOnlyList<string>? Tables = null,
    bool IncludeTimeline = false);

/// <summary>Where a cut extract resumes: the module and the last sequence written.</summary>
/// <param name="Module">The module name.</param>
/// <param name="Sequence">The last audit sequence number written for that module.</param>
public sealed record ExtractCursor(string Module, long Sequence)
{
    private const string Prefix = "x1.";

    /// <summary>The cursor as opaque text: <c>module:sequence</c> as base64url with the prefix <c>x1.</c>.</summary>
    /// <returns>The text.</returns>
    public string Encode() => Prefix + CursorCodec.Encode(string.Empty, Module + ":" + Sequence.ToString(CultureInfo.InvariantCulture));

    /// <summary>Reads a cursor; null when the text is malformed or was not written by <see cref="Encode"/>.</summary>
    /// <param name="text">The cursor text.</param>
    /// <returns>The cursor, or null.</returns>
    public static ExtractCursor? Decode(string? text)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal) || !CursorCodec.TryDecode(text[Prefix.Length..], string.Empty, out var plain))
        {
            return null;
        }

        var colon = plain.LastIndexOf(':');
        if (colon <= 0 || colon > 65 || !long.TryParse(plain.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return null;
        }

        return new ExtractCursor(plain[..colon], sequence);
    }
}

/// <summary>One audit row of the extract. The JSON columns are masked.</summary>
/// <param name="Module">The module.</param>
/// <param name="Sequence">The sequence number inside the module.</param>
/// <param name="OccurredAtUtc">When it happened.</param>
/// <param name="ActorUserId">Who did it.</param>
/// <param name="Action">What was done.</param>
/// <param name="ResourceType">The table.</param>
/// <param name="ResourceId">The key of the record.</param>
/// <param name="OldValues">The values before (masked JSON).</param>
/// <param name="NewValues">The values after (masked JSON).</param>
/// <param name="Metadata">Extra details (masked JSON).</param>
/// <param name="PayloadHash">The hash of the row, when hashed.</param>
public sealed record ExtractRow(
    string Module,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string ActorUserId,
    string Action,
    string ResourceType,
    string ResourceId,
    string? OldValues,
    string? NewValues,
    string? Metadata,
    string? PayloadHash);

/// <summary>One batch of an extract.</summary>
/// <param name="Rows">The rows.</param>
/// <param name="NextCursor">Where to continue; null when <paramref name="Done"/>.</param>
/// <param name="Done">True when nothing more can follow.</param>
public sealed record ExtractBatch(IReadOnlyList<ExtractRow> Rows, string? NextCursor, bool Done);

/// <summary>One column of the extract file.</summary>
/// <param name="Header">The header text.</param>
/// <param name="Value">Reads the cell text.</param>
/// <param name="IsText">True for free text (formula-neutralized in CSV).</param>
public sealed record ExtractColumn(string Header, Func<ExtractRow, string?> Value, bool IsText = true);

/// <summary>Reads the audit rows of every module in a fixed order, in bounded batches.</summary>
public interface IExtractReader
{
    /// <summary>The fixed column list of the file: Module, Sequence, OccurredAtUtc, ActorUserId, Action, ResourceType, ResourceId, OldValues, NewValues, Metadata, PayloadHash.</summary>
    IReadOnlyList<ExtractColumn> Columns { get; }

    /// <summary>Reads the next batch: modules in ordinal order, inside a module by sequence.</summary>
    /// <param name="query">The query (with the cursor of the previous batch).</param>
    /// <param name="take">The batch size, 1 to 5000.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The batch, or <c>ChronoAudit.Validation</c>, <c>Extract.InvalidTable</c> or <c>ChronoAudit.InvalidCursor</c>.</returns>
    Task<ChronoAuditResult<ExtractBatch>> NextAsync(ExtractQuery query, int take, CancellationToken cancellationToken);
}

internal sealed partial class ExtractReader(
    IAuditHistory history, SensitiveDataMasker masker, PersonalDataMasking personalData, ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory, IServiceProvider services) : IExtractReader
{
    private const int MaxRawBatchesPerCall = 20;
    internal const string TimelineModule = "timeline";

    public IReadOnlyList<ExtractColumn> Columns { get; } =
    [
        new("Module", r => r.Module),
        new("Sequence", r => r.Sequence.ToString(CultureInfo.InvariantCulture), IsText: false),
        new("OccurredAtUtc", r => CsvWriter.Instant(r.OccurredAtUtc), IsText: false),
        new("ActorUserId", r => r.ActorUserId),
        new("Action", r => r.Action),
        new("ResourceType", r => r.ResourceType),
        new("ResourceId", r => r.ResourceId),
        new("OldValues", r => r.OldValues),
        new("NewValues", r => r.NewValues),
        new("Metadata", r => r.Metadata),
        new("PayloadHash", r => r.PayloadHash),
    ];

    public async Task<ChronoAuditResult<ExtractBatch>> NextAsync(ExtractQuery query, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (take < 1 || take > IModuleAuditHistorySource.MaxExtractPageSize)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"The batch size must be between 1 and {IModuleAuditHistorySource.MaxExtractPageSize}.");
        }

        if (query.To <= query.From)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The end of the range must be later than its start.");
        }

        if (query.To - query.From > TimeSpan.FromDays(366))
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The range may be at most 366 days.");
        }

        var tablesByModule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.Tables ?? [])
        {
            var match = TableItem().Match(item ?? string.Empty);
            if (!match.Success)
            {
                return ChronoAuditError.Validation("Extract.InvalidTable", "A table must be written as module/table.");
            }

            if (!tablesByModule.TryGetValue(match.Groups[1].Value, out var list))
            {
                tablesByModule[match.Groups[1].Value] = list = [];
            }

            list.Add(match.Groups[2].Value);
        }

        var wanted = (query.Modules is { Count: > 0 } ? query.Modules : null) ?? (tablesByModule.Count > 0 ? tablesByModule.Keys.ToList() : null);
        var known = history.Modules;
        var modules = new List<string>();
        foreach (var name in wanted ?? known)
        {
            var actual = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (actual is null)
            {
                return ChronoAuditError.Validation("ChronoAudit.Validation", $"The module '{name}' is not an audited module.");
            }

            if (!modules.Contains(actual, StringComparer.Ordinal))
            {
                modules.Add(actual);
            }
        }

        modules.Sort(StringComparer.Ordinal);

        if (query.IncludeTimeline)
        {
            if (!registry.TimelineAdded || services.GetService<ChronoAuditStoreRegistration>() is null)
            {
                return ChronoAuditError.Validation("ChronoAudit.Validation", "The timeline is not enabled; call AddTimeline().");
            }

            if (modules.Contains(TimelineModule, StringComparer.OrdinalIgnoreCase))
            {
                return ChronoAuditError.Validation("ChronoAudit.Validation", "An audited module cannot be named 'timeline' when the timeline is included.");
            }

            // The events come first, then the field changes module by module.
            modules.Insert(0, TimelineModule);
        }

        var index = 0;
        long after = 0;
        if (query.Cursor is not null)
        {
            var cursor = ExtractCursor.Decode(query.Cursor);
            index = cursor is null ? -1 : modules.FindIndex(m => string.Equals(m, cursor.Module, StringComparison.Ordinal));
            if (index < 0)
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }

            after = cursor!.Sequence;
        }

        var rows = new List<ExtractRow>();
        string? lastModule = null;
        var rawBatches = 0;
        while (index < modules.Count && rows.Count == 0 && rawBatches < MaxRawBatchesPerCall)
        {
            var module = modules[index];
            if (string.Equals(module, TimelineModule, StringComparison.Ordinal))
            {
                var events = await ReadTimelineAsync(query, after, take, cancellationToken);
                rawBatches++;
                if (events.Count == 0)
                {
                    index++;
                    after = 0;
                    continue;
                }

                lastModule = module;
                after = events[^1].Sequence;
                rows.AddRange(events);
                continue;
            }

            var source = history.Find(module)!;
            var tables = tablesByModule.TryGetValue(module, out var chosen) ? chosen : null;
            var raw = tables is null
                ? await source.ListModuleChangesAsync(query.From, query.To, after, take, cancellationToken)
                : await source.ListModuleChangesAsync(query.From, query.To, after, take, tables, cancellationToken);
            rawBatches++;

            if (raw.Count == 0)
            {
                index++;
                after = 0;
                continue;
            }

            lastModule = module;
            after = raw[^1].Row.Sequence;
            foreach (var item in raw)
            {
                if ((query.ResourceType is null || string.Equals(item.ResourceType, query.ResourceType, StringComparison.Ordinal))
                    && (query.ActorUserId is null || string.Equals(item.Row.ActorUserId, query.ActorUserId, StringComparison.Ordinal)))
                {
                    rows.Add(ToRow(module, item));
                }
            }
        }

        // Exhausted: every module was read to its end. Otherwise continue after the last row that was read (even when the filter dropped it).
        if (index >= modules.Count)
        {
            return new ExtractBatch(rows, null, true);
        }

        return new ExtractBatch(rows, new ExtractCursor(lastModule ?? modules[index], after).Encode(), false);
    }

    // The timeline events by id, inside the range; the resource type and actor filters apply to subject type and actor.
    private async Task<List<ExtractRow>> ReadTimelineAsync(ExtractQuery query, long after, int take, CancellationToken cancellationToken)
    {
        var registration = services.GetRequiredService<ChronoAuditStoreRegistration>();
        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        var events = db.Set<TimelineEvent>().AsNoTracking().Where(e => e.Id > after && e.OccurredAtUtc >= query.From && e.OccurredAtUtc <= query.To);
        if (query.ResourceType is { } type)
        {
            events = events.Where(e => e.SubjectType == type);
        }

        if (query.ActorUserId is { } actor)
        {
            events = events.Where(e => e.ActorUserId == actor);
        }

        var page = await events.OrderBy(e => e.Id).Take(take)
            .Select(e => new { e.Id, e.SourceEventId, e.EventType, e.SubjectType, e.SubjectId, e.GroupKey, e.OccurredAtUtc, e.ActorUserId, e.ActorDisplayName, e.Payload })
            .ToListAsync(cancellationToken);
        return page.Select(e => new ExtractRow(
            TimelineModule,
            e.Id,
            e.OccurredAtUtc,
            e.ActorUserId ?? e.ActorDisplayName,
            e.EventType,
            e.SubjectType,
            e.SubjectId,
            null,
            Mask(e.Payload),
            System.Text.Json.JsonSerializer.Serialize(new { sourceEventId = e.SourceEventId, groupKey = e.GroupKey }),
            null)).ToList();
    }

    [GeneratedRegex("^([A-Za-z0-9_-]{1,64})/([A-Za-z0-9_]{1,128})$", RegexOptions.CultureInvariant)]
    private static partial Regex TableItem();

    private ExtractRow ToRow(string module, ModuleAuditRow item)
    {
        var row = item.Row;
        return new ExtractRow(
            module,
            row.Sequence,
            row.OccurredAtUtc,
            row.ActorUserId,
            row.Action,
            item.ResourceType,
            item.ResourceId,
            Mask(row.OldValuesJson),
            Mask(row.NewValuesJson),
            row.MetadataJson is null ? null : masker.MaskJson(row.MetadataJson, codeIsSecret: false),
            row.PayloadHash);
    }

    // Secrets by name, then personal fields (never revealed in an extract).
    private string? Mask(string? json) => json is null ? null : personalData.MaskJson(masker.MaskJson(json, codeIsSecret: false), reveal: false);
}

/// <summary>Writes an extract as a CSV file.</summary>
public static class ExtractCsv
{
    private const int BatchSize = IModuleAuditHistorySource.MaxExtractPageSize;

    /// <summary>
    /// Writes the BOM, the header and every batch, hashing the exact bytes written. Memory stays bounded whatever the size.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="query">The query (its cursor resumes a cut file; the BOM and header are then written again).</param>
    /// <param name="output">The destination stream.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of rows written and the hex SHA-256 of the bytes written.</returns>
    /// <exception cref="InvalidOperationException">The reader refused the query (the message carries the error code).</exception>
    public static async Task<(long Rows, string Sha256)> WriteAsync(IExtractReader reader, ExtractQuery query, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(output);

        var columns = reader.Columns.Select(c => new CsvColumn<ExtractRow>(c.Header, c.Value, c.IsText)).ToList();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;

        await WriteAsync(output, hash, CsvWriter.Bom.ToArray(), cancellationToken);
        await WriteAsync(output, hash, Encoding.UTF8.GetBytes(CsvWriter.HeaderLine(columns)), cancellationToken);

        var current = query;
        while (true)
        {
            var result = await reader.NextAsync(current, BatchSize, cancellationToken);
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"{result.Error.Code}: {result.Error.Message}");
            }

            var batch = result.Value;
            if (batch.Rows.Count > 0)
            {
                var text = new StringBuilder();
                foreach (var row in batch.Rows)
                {
                    text.Append(CsvWriter.Line(row, columns));
                }

                await WriteAsync(output, hash, Encoding.UTF8.GetBytes(text.ToString()), cancellationToken);
                count += batch.Rows.Count;
            }

            if (batch.Done || batch.NextCursor is null)
            {
                break;
            }

            current = current with { Cursor = batch.NextCursor };
        }

        return (count, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task WriteAsync(Stream output, IncrementalHash hash, byte[] bytes, CancellationToken cancellationToken)
    {
        await output.WriteAsync(bytes, cancellationToken);
        hash.AppendData(bytes);
    }
}
