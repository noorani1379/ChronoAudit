using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Where the rows of an analytics subject come from. The source applies the access scope in its own <c>Where</c>, before any filter of the caller.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <param name="services">The services of a new scope; resolve the database context from them.</param>
/// <param name="scope">The caller's data scope.</param>
/// <returns>The scoped rows.</returns>
public delegate IQueryable<TRow> AnalyticsSource<TRow>(IServiceProvider services, ChronoAuditScope scope);

/// <summary>Runs analytics queries over the registered subjects.</summary>
public interface IAnalyticsService
{
    /// <summary>The subjects a caller of this scope may run: a subject that needs the All scope is hidden from every other scope.</summary>
    /// <param name="scope">The caller's data scope.</param>
    /// <returns>The catalog.</returns>
    IReadOnlyList<AnalyticsCatalogEntry> Catalog(ChronoAuditScope scope);

    /// <summary>Runs one query: one database statement (two when the previous period is compared).</summary>
    /// <param name="query">The query.</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result, or <c>Analytics.SubjectNotFound</c>, <c>Analytics.SubjectForbidden</c>, <c>Filter.NotAllowed</c>, <c>Analytics.RangeTooWide</c>.</returns>
    Task<ChronoAuditResult<AnalyticsResult>> RunAsync(AnalyticsQuery query, ChronoAuditScope scope, CancellationToken cancellationToken);
}

/// <summary>Checks a query against its subject and the caller's scope without running it.</summary>
internal interface IAnalyticsPlanner
{
    ChronoAuditResult Validate(AnalyticsQuery query, ChronoAuditScope scope);
}

/// <summary>One subject as the host registered it: the generic definition and where its rows come from.</summary>
internal abstract class RegisteredAnalyticsSubject
{
    protected RegisteredAnalyticsSubject()
    {
    }

    internal abstract string Key { get; }

    internal abstract bool RequiresAllScope { get; }

    internal abstract AnalyticsCatalogEntry Describe();

    internal abstract ChronoAuditResult Validate(AnalyticsQuery query, IServiceProvider services, ChronoAuditScope scope);

    internal abstract Task<ChronoAuditResult<AnalyticsResult>> RunAsync(AnalyticsQuery query, IServiceProvider services, ChronoAuditScope scope, CancellationToken cancellationToken);
}

internal sealed class RegisteredAnalyticsSubject<TRow>(AnalyticsSubject<TRow> subject, AnalyticsSource<TRow> source) : RegisteredAnalyticsSubject
    where TRow : class
{
    internal override string Key => subject.Key;

    internal override bool RequiresAllScope => subject.RequiresAllScope;

    internal override AnalyticsCatalogEntry Describe() => subject.Describe();

    internal override ChronoAuditResult Validate(AnalyticsQuery query, IServiceProvider services, ChronoAuditScope scope)
    {
        var plan = subject.Plan(query, source(services, scope).AsNoTracking());
        return plan.IsFailure ? ChronoAuditResult.Failure(plan.Error) : ChronoAuditResult.Success();
    }

    internal override async Task<ChronoAuditResult<AnalyticsResult>> RunAsync(
        AnalyticsQuery query, IServiceProvider services, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        var plan = subject.Plan(query, source(services, scope).AsNoTracking());
        if (plan.IsFailure)
        {
            return ChronoAuditResult.Failure<AnalyticsResult>(plan.Error);
        }

        // One statement for the groups, and one more of the same shape for the previous period when it was asked for (never a loop).
        var cells = await plan.Value.Cells.ToListAsync(cancellationToken);
        var previous = plan.Value.PreviousCells is { } previousQuery ? await previousQuery.ToListAsync(cancellationToken) : null;
        return ChronoAuditResult.Success(plan.Value.Shape(cells, previous));
    }
}

internal sealed class AnalyticsService(ChronoAuditRegistry registry, IServiceScopeFactory scopeFactory) : IAnalyticsService, IAnalyticsPlanner
{
    private readonly object _gate = new();
    private bool _builtIn;

    private void EnsureBuiltIn()
    {
        if (!registry.BuiltInAnalyticsRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (_builtIn)
            {
                return;
            }

            ChronoAuditAnalyticsSubjects.AddTo(registry);
            _builtIn = true;
        }
    }

    public IReadOnlyList<AnalyticsCatalogEntry> Catalog(ChronoAuditScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureBuiltIn();
        return registry.AnalyticsSubjects.Where(s => !s.RequiresAllScope || scope.Kind == ScopeKind.All).Select(s => s.Describe()).ToList();
    }

    public async Task<ChronoAuditResult<AnalyticsResult>> RunAsync(AnalyticsQuery query, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        var subject = Find(query, scope, out var error);
        if (subject is null)
        {
            return error!;
        }

        // The source gets a scope of its own, so the context it reads is not shared with the caller.
        using var serviceScope = scopeFactory.CreateScope();
        return await subject.RunAsync(query, serviceScope.ServiceProvider, scope, cancellationToken);
    }

    public ChronoAuditResult Validate(AnalyticsQuery query, ChronoAuditScope scope)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        var subject = Find(query, scope, out var error);
        if (subject is null)
        {
            return ChronoAuditResult.Failure(error!);
        }

        using var serviceScope = scopeFactory.CreateScope();
        return subject.Validate(query, serviceScope.ServiceProvider, scope);
    }

    private RegisteredAnalyticsSubject? Find(AnalyticsQuery query, ChronoAuditScope scope, out ChronoAuditError? error)
    {
        error = null;
        EnsureBuiltIn();
        var subject = registry.AnalyticsSubjects.FirstOrDefault(s => string.Equals(s.Key, query.Subject, StringComparison.Ordinal));
        if (subject is null)
        {
            error = AnalyticsErrors.UnknownSubject(query.Subject);
            return null;
        }

        if (subject.RequiresAllScope && scope.Kind != ScopeKind.All)
        {
            error = ChronoAuditError.Forbidden("Analytics.SubjectForbidden", "This analytics subject needs the All scope.");
            return null;
        }

        return subject;
    }
}
