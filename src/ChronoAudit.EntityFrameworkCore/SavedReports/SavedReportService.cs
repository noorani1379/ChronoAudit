using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>What a caller sends to save or replace a report.</summary>
/// <param name="Title">The title.</param>
/// <param name="Definition">The analytics query.</param>
/// <param name="SharedWithGroupId">The group to share with; null keeps the report private.</param>
public sealed record SavedReportInput(string Title, AnalyticsQuery Definition, string? SharedWithGroupId = null);

/// <summary>A saved report as a caller sees it.</summary>
/// <param name="Id">The report id.</param>
/// <param name="Title">The title.</param>
/// <param name="Subject">The analytics subject key.</param>
/// <param name="Definition">The query; null in a list (read one report to get it) or when the stored text no longer reads as a query.</param>
/// <param name="IsShared">True when shared with a group.</param>
/// <param name="IsMine">True when the caller owns the report.</param>
/// <param name="CreatedAtUtc">When it was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed.</param>
/// <param name="ConcurrencyStamp">Send it back with an update or a delete to detect a concurrent change.</param>
public sealed record SavedReportDto(
    Guid Id, string Title, string Subject, AnalyticsQuery? Definition, bool IsShared, bool IsMine, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid ConcurrencyStamp);

/// <summary>Saves, lists, changes and runs analytics definitions. Ownership and sharing are enforced inside the queries.</summary>
public interface ISavedReportService
{
    /// <summary>Saves a new report owned by the caller.</summary>
    /// <param name="input">The report.</param>
    /// <param name="scope">The caller's data scope (its user id is the owner).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report, or the analytics refusal of the definition, <c>SavedReport.ShareNotAllowed</c>, <c>SavedReport.TitleInvalid</c>, <c>SavedReport.DefinitionInvalid</c>.</returns>
    Task<ChronoAuditResult<SavedReportDto>> CreateAsync(SavedReportInput input, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Reads one report the caller may see.</summary>
    /// <param name="id">The report id.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report with its definition, or <c>SavedReport.NotFound</c>.</returns>
    Task<ChronoAuditResult<SavedReportDto>> GetAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Lists the reports the caller may see, last changed first, without their definitions.</summary>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="limit">How many to return; clamped to 1..100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The reports.</returns>
    Task<ChronoAuditResult<IReadOnlyList<SavedReportDto>>> ListAsync(ChronoAuditScope scope, int limit, CancellationToken cancellationToken);

    /// <summary>Replaces a report of the caller.</summary>
    /// <param name="id">The report id.</param>
    /// <param name="input">The new content.</param>
    /// <param name="expectedStamp">The stamp the caller read; null skips the check.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The report, or <c>SavedReport.NotFound</c>, <c>SavedReport.NotOwner</c>, <c>SavedReport.Conflict</c> and the errors of <see cref="CreateAsync"/>.</returns>
    Task<ChronoAuditResult<SavedReportDto>> UpdateAsync(Guid id, SavedReportInput input, Guid? expectedStamp, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Deletes a report of the caller.</summary>
    /// <param name="id">The report id.</param>
    /// <param name="expectedStamp">The stamp the caller read; null skips the check.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Success, or <c>SavedReport.NotFound</c>, <c>SavedReport.NotOwner</c>, <c>SavedReport.Conflict</c>.</returns>
    Task<ChronoAuditResult> DeleteAsync(Guid id, Guid? expectedStamp, ChronoAuditScope scope, CancellationToken cancellationToken);

    /// <summary>Runs a saved report, optionally over another period.</summary>
    /// <param name="id">The report id.</param>
    /// <param name="from">A new start of the period, or null for the saved one.</param>
    /// <param name="to">A new end of the period, or null for the saved one.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The analytics result, <c>SavedReport.NotFound</c>, <c>SavedReport.Outdated</c> (the definition no longer fits the catalog), or the analytics error.</returns>
    Task<ChronoAuditResult<AnalyticsResult>> RunAsync(Guid id, DateTimeOffset? from, DateTimeOffset? to, ChronoAuditScope scope, CancellationToken cancellationToken);
}

internal sealed class SavedReportService(
    ChronoAuditStoreRegistration registration, IServiceScopeFactory scopeFactory, IAnalyticsService analytics, IAnalyticsPlanner planner, TimeProvider clock) : ISavedReportService
{
    private const int MaxListed = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly ChronoAuditError NotFound = ChronoAuditError.NotFound("SavedReport.NotFound", "The saved report was not found.");
    private static readonly ChronoAuditError NotOwner = ChronoAuditError.Forbidden("SavedReport.NotOwner", "Only the owner of a report can change or delete it.");
    private static readonly ChronoAuditError ShareNotAllowed = ChronoAuditError.Forbidden("SavedReport.ShareNotAllowed", "A report can only be shared with the caller's own group.");
    private static readonly ChronoAuditError Conflict = ChronoAuditError.Conflict("SavedReport.Conflict", "The report was changed by someone else meanwhile; reload it and try again.");

    public async Task<ChronoAuditResult<SavedReportDto>> CreateAsync(SavedReportInput input, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.UserId is null)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "A report needs a signed-in owner.");
        }

        var checkedInput = Check(input, scope);
        if (checkedInput.IsFailure)
        {
            return checkedInput.Error;
        }

        var created = SavedReport.Create(scope.UserId, input.Title, input.Definition.Subject, JsonSerializer.Serialize(input.Definition, Json), input.SharedWithGroupId, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        using var serviceScope = scopeFactory.CreateScope();
        var db = Db(serviceScope);
        db.Set<SavedReport>().Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(created.Value, scope, withDefinition: true);
    }

    public async Task<ChronoAuditResult<SavedReportDto>> GetAsync(Guid id, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var report = await Visible(Db(serviceScope).Set<SavedReport>().AsNoTracking(), scope).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return report is null ? NotFound : ToDto(report, scope, withDefinition: true);
    }

    public async Task<ChronoAuditResult<IReadOnlyList<SavedReportDto>>> ListAsync(ChronoAuditScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var take = Math.Clamp(limit, 1, MaxListed);
        using var serviceScope = scopeFactory.CreateScope();

        // The definition JSON is not read: the list shows titles only.
        var rows = await Visible(Db(serviceScope).Set<SavedReport>().AsNoTracking(), scope)
            .OrderByDescending(r => r.UpdatedAtUtc)
            .ThenBy(r => r.Id)
            .Take(take)
            .Select(r => new { r.Id, r.Title, r.Subject, r.OwnerUserId, r.SharedWithGroupId, r.CreatedAtUtc, r.UpdatedAtUtc, r.ConcurrencyStamp })
            .ToListAsync(cancellationToken);
        IReadOnlyList<SavedReportDto> list = rows
            .Select(r => new SavedReportDto(r.Id, r.Title, r.Subject, null, r.SharedWithGroupId is not null, IsOwner(r.OwnerUserId, scope), r.CreatedAtUtc, r.UpdatedAtUtc, r.ConcurrencyStamp))
            .ToList();
        return ChronoAuditResult.Success(list);
    }

    public async Task<ChronoAuditResult<SavedReportDto>> UpdateAsync(Guid id, SavedReportInput input, Guid? expectedStamp, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var db = Db(serviceScope);
        var report = await Visible(db.Set<SavedReport>(), scope).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return NotFound;
        }

        if (!IsOwner(report.OwnerUserId, scope))
        {
            return NotOwner;
        }

        if (expectedStamp is { } stamp && stamp != report.ConcurrencyStamp)
        {
            return Conflict;
        }

        var checkedInput = Check(input, scope);
        if (checkedInput.IsFailure)
        {
            return checkedInput.Error;
        }

        var changed = report.Change(input.Title, input.Definition.Subject, JsonSerializer.Serialize(input.Definition, Json), input.SharedWithGroupId, clock.GetUtcNow());
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict;
        }

        return ToDto(report, scope, withDefinition: true);
    }

    public async Task<ChronoAuditResult> DeleteAsync(Guid id, Guid? expectedStamp, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var serviceScope = scopeFactory.CreateScope();
        var db = Db(serviceScope);
        var report = await Visible(db.Set<SavedReport>(), scope).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (report is null)
        {
            return ChronoAuditResult.Failure(NotFound);
        }

        if (!IsOwner(report.OwnerUserId, scope))
        {
            return ChronoAuditResult.Failure(NotOwner);
        }

        if (expectedStamp is { } stamp && stamp != report.ConcurrencyStamp)
        {
            return ChronoAuditResult.Failure(Conflict);
        }

        db.Set<SavedReport>().Remove(report);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChronoAuditResult.Failure(Conflict);
        }

        return ChronoAuditResult.Success();
    }

    public async Task<ChronoAuditResult<AnalyticsResult>> RunAsync(Guid id, DateTimeOffset? from, DateTimeOffset? to, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        SavedReport? report;
        using (var serviceScope = scopeFactory.CreateScope())
        {
            report = await Visible(Db(serviceScope).Set<SavedReport>().AsNoTracking(), scope).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        }

        if (report is null)
        {
            return NotFound;
        }

        var definition = report.DefinitionVersion == SavedReport.CurrentDefinitionVersion ? Read(report.DefinitionJson) : null;
        if (definition is null)
        {
            return Outdated("The stored version or format can no longer be read.");
        }

        // A relative report: the caller may run the saved shape over another period.
        var result = await analytics.RunAsync(definition with { From = from ?? definition.From, To = to ?? definition.To }, scope, cancellationToken);

        // The definition is checked against the CURRENT catalog on every run: a key that was removed is "outdated", never a different report.
        return result.IsFailure && result.Error.Code == FilterErrors.Code ? Outdated(result.Error.Message) : result;
    }

    private static ChronoAuditError Outdated(string reason)
        => ChronoAuditError.Unprocessable("SavedReport.Outdated", $"The report definition no longer fits the current catalog and must be saved again: {reason}");

    // The sharing rule first, then the analytics validation: the definition must be runnable by the caller now.
    private ChronoAuditResult Check(SavedReportInput input, ChronoAuditScope scope)
    {
        if (input.Definition is null)
        {
            return ChronoAuditResult.Failure(ChronoAuditError.Validation("SavedReport.DefinitionInvalid", "The report definition is required."));
        }

        if (input.SharedWithGroupId is { } group)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                return ChronoAuditResult.Failure(ChronoAuditError.Validation("ChronoAudit.Validation", "The group id must not be blank."));
            }

            if (!(scope.Kind == ScopeKind.All || (scope.Kind == ScopeKind.Group && string.Equals(scope.GroupId, group, StringComparison.Ordinal))))
            {
                return ChronoAuditResult.Failure(ShareNotAllowed);
            }
        }

        return planner.Validate(input.Definition, scope);
    }

    // Visible to its owner, and when shared also to the All scope and to the Group scope of that group. Anything else is "not found",
    // even for All: a private report of someone else is invisible.
    private static IQueryable<SavedReport> Visible(IQueryable<SavedReport> rows, ChronoAuditScope scope)
    {
        var user = scope.UserId;
        var group = scope.GroupId;
        return scope.Kind switch
        {
            ScopeKind.All => rows.Where(r => r.OwnerUserId == user || r.SharedWithGroupId != null),
            ScopeKind.Group => rows.Where(r => r.OwnerUserId == user || (r.SharedWithGroupId != null && r.SharedWithGroupId == group)),
            _ => rows.Where(r => r.OwnerUserId == user),
        };
    }

    private static bool IsOwner(string ownerUserId, ChronoAuditScope scope) => scope.UserId is not null && string.Equals(ownerUserId, scope.UserId, StringComparison.Ordinal);

    private static SavedReportDto ToDto(SavedReport r, ChronoAuditScope scope, bool withDefinition) => new(
        r.Id, r.Title, r.Subject, withDefinition ? Read(r.DefinitionJson) : null, r.IsShared, IsOwner(r.OwnerUserId, scope), r.CreatedAtUtc, r.UpdatedAtUtc, r.ConcurrencyStamp);

    // Null when the stored text no longer reads as a definition: treated as outdated, never as an exception.
    private static AnalyticsQuery? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AnalyticsQuery>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private DbContext Db(IServiceScope serviceScope) => (DbContext)serviceScope.ServiceProvider.GetRequiredService(registration.ContextType);
}
