using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// One audit row of one entity: what changed, by whom, when. The old and new values are the JSON the audit writer stored (secrets are
/// already replaced by the mask; personal data is NOT: the reader decides what to show).
/// </summary>
public sealed record AuditHistoryRow(
    long Sequence, DateTimeOffset OccurredAtUtc, string ActorUserId, string Action, Guid CorrelationId,
    string? OldValuesJson, string? NewValuesJson, string? MetadataJson, string? PayloadHash);

/// <summary>The audit writer's spelling of the key of an entity with a single <c>Id</c> property; a different spelling would silently match nothing.</summary>
public static class AuditResourceKey
{
    /// <summary>The key text of a row whose single key property is <c>Id</c>.</summary>
    public static string Of(Guid id) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["Id"] = id });

    /// <summary>
    /// The stored resource id of a record from its key text: a JSON object of the key properties in key order, typed values (the form the audit
    /// writer uses). A composite key is written with its parts joined by <c>|</c>. False when the count of parts or a part does not fit.
    /// </summary>
    internal static bool TryOf(IReadOnlyList<(string Name, Type Clr)> key, string recordKey, out string resourceId)
    {
        resourceId = string.Empty;
        var parts = key.Count == 1 ? [recordKey] : recordKey.Split('|');
        if (parts.Length != key.Count)
        {
            return false;
        }

        var values = new Dictionary<string, object?>();
        for (var i = 0; i < parts.Length; i++)
        {
            var (name, clr) = key[i];
            var integer = NumberStyles.Integer;
            var invariant = CultureInfo.InvariantCulture;
            object? value;
            if (clr == typeof(Guid) && Guid.TryParse(parts[i], out var g)) value = g;
            else if (clr == typeof(int) && int.TryParse(parts[i], integer, invariant, out var n)) value = n;
            else if (clr == typeof(long) && long.TryParse(parts[i], integer, invariant, out var l)) value = l;
            else if (clr == typeof(short) && short.TryParse(parts[i], integer, invariant, out var s)) value = s;
            else if (clr == typeof(string)) value = parts[i];
            else return false;

            values[name] = value;
        }

        resourceId = JsonSerializer.Serialize(values);
        return true;
    }
}

/// <summary>One key column of an audited table: its name and a short type name (<c>guid</c>, <c>int</c>, <c>long</c>, <c>short</c>, <c>string</c>, or the CLR name).</summary>
public sealed record AuditedKeyPart(string Name, string Type);

/// <summary>One column of an audited table. <paramref name="Secret"/> is true when the audit writer stores its value masked (<c>[SensitiveData]</c>).</summary>
public sealed record AuditedColumn(string Name, string Type, bool Secret);

/// <summary>
/// A table of a module that the audit writer records automatically, found in the module's EF model (nobody registers it by hand).
/// <paramref name="RecordLookup"/> is true when one record can be asked for by its key (every key part is a guid, int, long, short or string);
/// a composite key is written as its parts joined with <c>|</c>, in the order of <paramref name="Key"/>.
/// </summary>
public sealed record AuditedTable(
    string Module, string Table, string Entity, IReadOnlyList<AuditedKeyPart> Key, IReadOnlyList<AuditedColumn> Columns,
    bool SoftDelete, bool AppendOnly, bool RecordLookup);

/// <summary>
/// Cross-module port: the audit history of ONE entity of one module, oldest first, read through the module's own database. The host
/// registers one source per module, so a reporting layer can show the field history of every module without touching another module's
/// DbContext. Keyset by the audit sequence number: a page is at most <see cref="MaxPageSize"/> rows, one indexed statement.
/// </summary>
public interface IModuleAuditHistorySource
{
    /// <summary>The largest page of one record or one table that a caller may ask for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The module name: for example <c>orders</c> or <c>billing</c>.</summary>
    string Module { get; }

    /// <summary>The audit rows of one entity found by its <see cref="Guid"/> key (single <c>Id</c> property), oldest first, keyset by the sequence.</summary>
    /// <param name="table">The table the entity is stored in (the audit row's <c>ResourceType</c>).</param>
    /// <param name="entityId">The entity's key.</param>
    /// <param name="afterSequence">Return only rows with a larger sequence (the cursor of the previous page); null for the first page.</param>
    /// <param name="limit">The page size, clamped to the largest page plus one.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<AuditHistoryRow>> ListChangesAsync(
        string table, Guid entityId, long? afterSequence, int limit, CancellationToken cancellationToken);

    /// <summary>The largest page the module-wide stream (extraction) may ask for.</summary>
    public const int MaxExtractPageSize = 5000;

    /// <summary>
    /// H4: the audit rows of the WHOLE module inside a time range, oldest first, keyset by the sequence number (the extraction tool
    /// streams them in batches). A source that cannot do it returns nothing.
    /// </summary>
    Task<IReadOnlyList<ModuleAuditRow>> ListModuleChangesAsync(
        DateTimeOffset from, DateTimeOffset to, long afterSequence, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModuleAuditRow>>([]);

    /// <summary>The audited tables of the module, discovered from its EF model. A source that cannot do it lists none.</summary>
    IReadOnlyList<AuditedTable> Tables => [];

    /// <summary>
    /// The history of ONE record of any audited table, found by its key (see <see cref="AuditedTable.RecordLookup"/>), oldest first, keyset by the
    /// sequence number. An unknown table, a key of the wrong shape or a record with no audit rows gives an empty list.
    /// </summary>
    Task<IReadOnlyList<AuditHistoryRow>> ListRecordChangesAsync(
        string table, string recordKey, long? afterSequence, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AuditHistoryRow>>([]);

    /// <summary>
    /// The audit rows of ALL the records of one table, oldest first, keyset by the sequence number, optionally inside a time range: the
    /// course of a whole table. An unknown table gives an empty list.
    /// </summary>
    Task<IReadOnlyList<ModuleAuditRow>> ListTableChangesAsync(
        string table, DateTimeOffset? from, DateTimeOffset? to, long afterSequence, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModuleAuditRow>>([]);

    /// <summary>
    /// <see cref="ListModuleChangesAsync(DateTimeOffset, DateTimeOffset, long, int, CancellationToken)"/> restricted to some tables. A source
    /// without table support filters the module's rows after reading them, so a page may then be shorter than <paramref name="limit"/> without being the last.
    /// </summary>
    async Task<IReadOnlyList<ModuleAuditRow>> ListModuleChangesAsync(
        DateTimeOffset from, DateTimeOffset to, long afterSequence, int limit, IReadOnlyCollection<string> tables, CancellationToken cancellationToken)
        => (await ListModuleChangesAsync(from, to, afterSequence, limit, cancellationToken)).Where(r => tables.Contains(r.ResourceType)).ToList();
}

/// <summary>One audit row together with the table and the key of the entity it belongs to.</summary>
public sealed record ModuleAuditRow(AuditHistoryRow Row, string ResourceType, string ResourceId);

/// <summary>The source of one module, on that module's <see cref="AuditableDbContext"/>. A singleton: it holds only the scope factory.</summary>
public sealed class AuditHistorySource<TDbContext>(string module, IServiceScopeFactory scopeFactory) : IModuleAuditHistorySource
    where TDbContext : AuditableDbContext
{
    private sealed record TableMeta(AuditedTable Info, IReadOnlyList<(string Name, Type Clr)> Key);

    // PublicationOnly: a failed discovery (for example a context that cannot be built yet) is not remembered for the life of the process.
    private readonly Lazy<List<TableMeta>> _meta = new(() => Discover(module, scopeFactory), LazyThreadSafetyMode.PublicationOnly);

    /// <inheritdoc />
    public string Module { get; } = module;

    private IReadOnlyList<AuditedTable>? _tables;

    /// <inheritdoc />
    public IReadOnlyList<AuditedTable> Tables => _tables ??= _meta.Value.Select(m => m.Info).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditHistoryRow>> ListChangesAsync(
        string table, Guid entityId, long? afterSequence, int limit, CancellationToken cancellationToken)
        => await ListByResourceIdAsync(table, AuditResourceKey.Of(entityId), afterSequence, limit, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditHistoryRow>> ListRecordChangesAsync(
        string table, string recordKey, long? afterSequence, int limit, CancellationToken cancellationToken)
    {
        var meta = Find(table);
        return meta is not null && TryResourceId(meta, recordKey, out var resourceId)
            ? await ListByResourceIdAsync(meta.Info.Table, resourceId, afterSequence, limit, cancellationToken)
            : [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleAuditRow>> ListTableChangesAsync(
        string table, DateTimeOffset? from, DateTimeOffset? to, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var meta = Find(table);
        if (meta is null)
        {
            return [];
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var name = meta.Info.Table;
        var query = db.AuditLogEntries.AsNoTracking().Where(a => a.ResourceType == name && a.SequenceNumber > afterSequence);
        if (from is { } start) query = query.Where(a => a.OccurredAtUtc >= start);
        if (to is { } end) query = query.Where(a => a.OccurredAtUtc <= end);

        return await query.OrderBy(a => a.SequenceNumber).Take(Math.Clamp(limit, 1, IModuleAuditHistorySource.MaxPageSize + 1))
            .Select(a => new ModuleAuditRow(
                new AuditHistoryRow(a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.CorrelationId, a.OldValues, a.NewValues, a.Metadata, a.PayloadHash),
                a.ResourceType, a.ResourceId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleAuditRow>> ListModuleChangesAsync(
        DateTimeOffset from, DateTimeOffset to, long afterSequence, int limit, CancellationToken cancellationToken)
        => await ListModuleChangesCoreAsync(from, to, afterSequence, limit, null, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModuleAuditRow>> ListModuleChangesAsync(
        DateTimeOffset from, DateTimeOffset to, long afterSequence, int limit, IReadOnlyCollection<string> tables, CancellationToken cancellationToken)
        => await ListModuleChangesCoreAsync(from, to, afterSequence, limit, tables.ToArray(), cancellationToken);

    private async Task<IReadOnlyList<ModuleAuditRow>> ListModuleChangesCoreAsync(
        DateTimeOffset from, DateTimeOffset to, long afterSequence, int limit, string[]? tables, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var take = Math.Clamp(limit, 1, IModuleAuditHistorySource.MaxExtractPageSize);

        var query = db.AuditLogEntries.AsNoTracking().Where(a => a.SequenceNumber > afterSequence && a.OccurredAtUtc >= from && a.OccurredAtUtc <= to);
        if (tables is not null)
        {
            query = query.Where(a => tables.Contains(a.ResourceType));
        }

        return await query.OrderBy(a => a.SequenceNumber).Take(take)
            .Select(a => new ModuleAuditRow(
                new AuditHistoryRow(a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.CorrelationId, a.OldValues, a.NewValues, a.Metadata, a.PayloadHash),
                a.ResourceType, a.ResourceId))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<AuditHistoryRow>> ListByResourceIdAsync(
        string table, string resourceId, long? afterSequence, int limit, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        // One row more than the largest page: the callers ask for limit + 1 to learn whether a next page exists, and a clamp at the page size would hide it.
        var take = Math.Clamp(limit, 1, IModuleAuditHistorySource.MaxPageSize + 1);

        var query = db.AuditLogEntries.AsNoTracking().Where(a => a.ResourceType == table && a.ResourceId == resourceId);
        if (afterSequence is { } after)
        {
            query = query.Where(a => a.SequenceNumber > after);
        }

        return await query.OrderBy(a => a.SequenceNumber).Take(take)
            .Select(a => new AuditHistoryRow(a.SequenceNumber, a.OccurredAtUtc, a.ActorUserId, a.Action, a.CorrelationId, a.OldValues, a.NewValues, a.Metadata, a.PayloadHash))
            .ToListAsync(cancellationToken);
    }

    private TableMeta? Find(string table)
        => _meta.Value.FirstOrDefault(m => string.Equals(m.Info.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the key the way the audit writer does (<c>GenerateAutoAuditLogs</c>: a JSON object of the primary-key properties in key order,
    /// typed values), so the lookup is an equality on <c>ResourceId</c>. A key with the wrong number of parts or a part that does not parse matches nothing.
    /// </summary>
    private static bool TryResourceId(TableMeta meta, string recordKey, out string resourceId)
    {
        resourceId = string.Empty;
        return meta.Info.RecordLookup && AuditResourceKey.TryOf(meta.Key, recordKey, out resourceId);
    }

    private static List<TableMeta> Discover(string module, IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        return db.Model.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned() && t.GetTableName() is not null && t.FindPrimaryKey() is not null
                        && !db.IsAuditExcluded(t.ClrType))
            .Select(t => ToMeta(module, t))
            .OrderBy(m => m.Info.Table, StringComparer.Ordinal)
            .ToList();
    }

    private static TableMeta ToMeta(string module, IEntityType type)
    {
        var key = type.FindPrimaryKey()!.Properties.Select(p => (p.Name, Clr: Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType)).ToList();
        var lookup = key.All(k => k.Clr == typeof(Guid) || k.Clr == typeof(int) || k.Clr == typeof(long) || k.Clr == typeof(short) || k.Clr == typeof(string));

        var info = new AuditedTable(
            module, type.GetTableName()!, type.ShortName(),
            key.Select(k => new AuditedKeyPart(k.Name, TypeName(k.Clr))).ToList(),
            type.GetProperties().Select(p => new AuditedColumn(
                p.Name, TypeName(Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType), p.PropertyInfo?.IsDefined(typeof(SensitiveDataAttribute), true) ?? false)).ToList(),
            typeof(IAuditableEntity).IsAssignableFrom(type.ClrType), typeof(IAppendOnly).IsAssignableFrom(type.ClrType), lookup);

        return new TableMeta(info, key);
    }

    private static string TypeName(Type clr) => clr == typeof(Guid) ? "guid" : clr == typeof(int) ? "int" : clr == typeof(long) ? "long"
        : clr == typeof(short) ? "short" : clr == typeof(string) ? "string" : clr == typeof(bool) ? "bool" : clr == typeof(decimal) ? "decimal"
        : clr == typeof(DateTimeOffset) || clr == typeof(DateTime) ? "datetime" : clr.IsEnum ? "enum" : clr.Name;
}

/// <summary>All history sources of the host, by module name.</summary>
public interface IAuditHistory
{
    /// <summary>The module names, in registration order.</summary>
    IReadOnlyList<string> Modules { get; }

    /// <summary>Finds a source by module name (case-insensitive); null when there is none.</summary>
    /// <param name="module">The module name.</param>
    /// <returns>The source, or null.</returns>
    IModuleAuditHistorySource? Find(string module);
}

internal sealed class AuditHistorySources(IEnumerable<IModuleAuditHistorySource> sources) : IAuditHistory
{
    private readonly List<IModuleAuditHistorySource> _sources = sources.ToList();

    public IReadOnlyList<string> Modules => _sources.Select(s => s.Module).ToList();

    public IModuleAuditHistorySource? Find(string module)
        => _sources.FirstOrDefault(s => string.Equals(s.Module, module, StringComparison.OrdinalIgnoreCase));
}
