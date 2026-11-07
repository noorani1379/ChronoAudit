using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>A search over the API activity log (or over the mutation intents that have no result).</summary>
/// <param name="From">The start of the range (inclusive).</param>
/// <param name="To">The end of the range (inclusive); not before <paramref name="From"/>.</param>
/// <param name="UserId">Only calls of this user.</param>
/// <param name="SessionId">Only calls of this session.</param>
/// <param name="CorrelationId">Only calls with this correlation id.</param>
/// <param name="EndpointKey">Only calls of this endpoint (route template).</param>
/// <param name="Method">Only this HTTP method (case-insensitive).</param>
/// <param name="StatusCode">Only this exact status code.</param>
/// <param name="StatusClass">Only this class: <c>2xx</c>, <c>3xx</c>, <c>4xx</c> or <c>5xx</c>.</param>
/// <param name="Ip">Only this client address.</param>
/// <param name="IncompleteOnly">Search the mutation intents that have no result row instead.</param>
/// <param name="Before">The cursor of the previous page (the last id).</param>
/// <param name="Limit">The page size, 1 to 200.</param>
public sealed record ActivitySearchQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    string? UserId = null,
    string? SessionId = null,
    Guid? CorrelationId = null,
    string? EndpointKey = null,
    string? Method = null,
    int? StatusCode = null,
    string? StatusClass = null,
    string? Ip = null,
    bool IncompleteOnly = false,
    string? Before = null,
    int Limit = 50);

/// <summary>One call of the activity log.</summary>
/// <param name="Id">The row id.</param>
/// <param name="RequestId">The request id.</param>
/// <param name="OccurredAtUtc">When the call finished (for an incomplete intent: when it started).</param>
/// <param name="DurationMs">The duration.</param>
/// <param name="UserId">The user.</param>
/// <param name="ActAsUserId">The delegate.</param>
/// <param name="SessionId">The session.</param>
/// <param name="CorrelationId">The correlation id.</param>
/// <param name="HttpMethod">The method.</param>
/// <param name="Path">The path.</param>
/// <param name="EndpointKey">The route template.</param>
/// <param name="StatusCode">The status code; 0 for an intent without a result.</param>
/// <param name="ErrorCode">The error code.</param>
/// <param name="IpAddress">The client address.</param>
/// <param name="HasBody">Whether a body is stored.</param>
/// <param name="PreviousHash">The previous hash.</param>
/// <param name="PayloadHash">The hash of the row.</param>
public sealed record ActivityRow(
    long Id,
    Guid RequestId,
    DateTimeOffset OccurredAtUtc,
    int DurationMs,
    string? UserId,
    string? ActAsUserId,
    string? SessionId,
    Guid? CorrelationId,
    string HttpMethod,
    string Path,
    string? EndpointKey,
    short StatusCode,
    string? ErrorCode,
    string? IpAddress,
    bool HasBody,
    string? PreviousHash,
    string? PayloadHash);

/// <summary>One page of the activity search, newest first.</summary>
/// <param name="Items">The rows.</param>
/// <param name="NextCursor">The cursor of the next page; null on the last page.</param>
/// <param name="AsOfUtc">The time the page was read.</param>
public sealed record ActivityPage(IReadOnlyList<ActivityRow> Items, string? NextCursor, DateTimeOffset AsOfUtc);

/// <summary>The stored body of one call.</summary>
/// <param name="LogId">The log row id.</param>
/// <param name="RequestId">The request id.</param>
/// <param name="ContentType">The content type.</param>
/// <param name="Body">The masked body.</param>
/// <param name="IsTruncated">Whether the body was cut.</param>
/// <param name="FileMetadata">The multipart description.</param>
public sealed record ActivityBody(long LogId, Guid RequestId, string? ContentType, string? Body, bool IsTruncated, string? FileMetadata);

/// <summary>Reads the API activity log.</summary>
public interface IActivitySearch
{
    /// <summary>Runs one page of the search.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The page, or <c>ChronoAudit.Validation</c> or <c>ChronoAudit.InvalidCursor</c>.</returns>
    Task<ChronoAuditResult<ActivityPage>> SearchAsync(ActivitySearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the stored body of one call. The view is audited first: if the audit row cannot be saved, the body is not returned.
    /// </summary>
    /// <param name="logId">The log row id.</param>
    /// <param name="viewerUserId">Who views the body (written to the audit row).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The body, <c>ActivityLog.NotFound</c>, or <c>ChronoAudit.AuditUnavailable</c>.</returns>
    Task<ChronoAuditResult<ActivityBody>> ViewBodyAsync(long logId, string viewerUserId, CancellationToken cancellationToken);

    /// <summary>Counts the log rows a query matches, stopping at <paramref name="upTo"/> + 1 (so a caller can test a limit cheaply). The query rules of <see cref="SearchAsync"/> apply; the cursor and the limit are ignored.</summary>
    /// <param name="query">The query.</param>
    /// <param name="upTo">The limit to test against.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The count, at most <paramref name="upTo"/> + 1; or the validation error of the query.</returns>
    Task<ChronoAuditResult<long>> CountAsync(ActivitySearchQuery query, int upTo, CancellationToken cancellationToken);

    /// <summary>The mutation intents older than the configured age that have no result row, oldest first.</summary>
    /// <param name="limit">At most this many (1 to 500).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The intents.</returns>
    Task<IReadOnlyList<ApiMutationIntent>> IncompleteIntentsAsync(int limit, CancellationToken cancellationToken);
}

internal sealed class ActivitySearchService(
    ChronoAuditStoreRegistration registration,
    ChronoAuditRegistry auditRegistry,
    IServiceScopeFactory scopeFactory,
    ActivityLogOptions options,
    TimeProvider clock) : IActivitySearch
{
    private const int MaxLimit = 200;
    private const int NarrowDays = 31;
    private const int WideDays = 366;
    private const string CursorPrefix = "i1:";

    public async Task<ChronoAuditResult<ActivityPage>> SearchAsync(ActivitySearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var invalid = Validate(query, out var before);
        if (invalid is not null)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        var method = query.Method?.Trim().ToUpperInvariant();
        var endpoint = query.EndpointKey?.Trim();

        if (query.IncompleteOnly)
        {
            var cutoff = now - TimeSpan.FromMinutes(options.IncompleteIntentAgeMinutes);
            var intents = IncompleteQuery(db, cutoff).Where(i => i.StartedAtUtc >= query.From && i.StartedAtUtc <= query.To);
            if (query.UserId is not null) { var u = query.UserId; intents = intents.Where(i => i.UserId == u); }
            if (query.SessionId is not null) { var s = query.SessionId; intents = intents.Where(i => i.SessionId == s); }
            if (query.CorrelationId is { } c) { intents = intents.Where(i => i.CorrelationId == c); }
            if (endpoint is not null) { intents = intents.Where(i => i.EndpointKey == endpoint); }
            if (method is not null) { intents = intents.Where(i => i.Method == method); }
            if (before is { } b) { intents = intents.Where(i => i.Id < b); }

            var rows = await intents.OrderByDescending(i => i.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
            var more = rows.Count > query.Limit;
            var items = rows.Take(query.Limit).Select(i => new ActivityRow(
                i.Id, i.RequestId, i.StartedAtUtc, 0, i.UserId, i.ActAsUserId, i.SessionId, i.CorrelationId, i.Method, i.Path, i.EndpointKey,
                0, null, null, false, i.PreviousHash, i.PayloadHash)).ToList();
            return new ActivityPage(items, more ? Cursor(items[^1].Id) : null, now);
        }

        var logs = FilterLogs(db, query, method, endpoint);
        if (before is { } beforeId) { logs = logs.Where(l => l.Id < beforeId); }

        var page = await logs.OrderByDescending(l => l.Id).Take(query.Limit + 1)
            .Select(l => new ActivityRow(
                l.Id, l.RequestId, l.OccurredAtUtc, l.DurationMs, l.UserId, l.ActAsUserId, l.SessionId, l.CorrelationId, l.HttpMethod, l.Path, l.EndpointKey,
                l.StatusCode, l.ErrorCode, l.IpAddress, l.HasBody, l.PreviousHash, l.PayloadHash))
            .ToListAsync(cancellationToken);
        var hasMore = page.Count > query.Limit;
        var result = hasMore ? page.Take(query.Limit).ToList() : page;
        return new ActivityPage(result, hasMore ? Cursor(result[^1].Id) : null, now);
    }

    // The range and the filters of the request log search (everything except the cursor).
    private static IQueryable<ApiRequestLog> FilterLogs(DbContext db, ActivitySearchQuery query, string? method, string? endpoint)
    {
        var logs = db.Set<ApiRequestLog>().AsNoTracking().Where(l => l.OccurredAtUtc >= query.From && l.OccurredAtUtc <= query.To);
        if (query.UserId is not null) { var u = query.UserId; logs = logs.Where(l => l.UserId == u); }
        if (query.SessionId is not null) { var s = query.SessionId; logs = logs.Where(l => l.SessionId == s); }
        if (query.CorrelationId is { } cid) { logs = logs.Where(l => l.CorrelationId == cid); }
        if (endpoint is not null) { logs = logs.Where(l => l.EndpointKey == endpoint); }
        if (method is not null) { logs = logs.Where(l => l.HttpMethod == method); }
        if (query.StatusCode is { } code) { var status = (short)code; logs = logs.Where(l => l.StatusCode == status); }
        if (query.StatusClass is { } klass)
        {
            var low = (short)((klass[0] - '0') * 100);
            var high = (short)(low + 100);
            logs = logs.Where(l => l.StatusCode >= low && l.StatusCode < high);
        }

        if (query.Ip is not null) { var ip = query.Ip; logs = logs.Where(l => l.IpAddress == ip); }
        return logs;
    }

    public async Task<ChronoAuditResult<long>> CountAsync(ActivitySearchQuery query, int upTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(upTo, 0);
        var invalid = Validate(query, out _);
        if (invalid is not null)
        {
            return invalid;
        }

        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);

        // Counting stops one row past the limit: the caller only needs to know whether the limit is exceeded.
        return (long)await FilterLogs(db, query, query.Method?.Trim().ToUpperInvariant(), query.EndpointKey?.Trim()).Take(upTo + 1).CountAsync(cancellationToken);
    }

    public async Task<ChronoAuditResult<ActivityBody>> ViewBodyAsync(long logId, string viewerUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerUserId);
        using var scope = scopeFactory.CreateScope();
        var store = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        var found = await store.Set<ApiRequestBody>().AsNoTracking()
            .Where(b => b.ApiRequestLogId == logId)
            .Join(store.Set<ApiRequestLog>().AsNoTracking(), b => b.ApiRequestLogId, l => l.Id, (b, l) => new ActivityBody(l.Id, l.RequestId, b.ContentType, b.Body, b.IsTruncated, b.FileMetadata))
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return ChronoAuditError.NotFound("ActivityLog.NotFound", "No such request or it has no stored body.");
        }

        // Evidence first: the view is recorded in the audit table before the body leaves. No audited module, no view.
        if (auditRegistry.Modules.Count == 0)
        {
            return AuditUnavailable();
        }

        try
        {
            var audit = (AuditableDbContext)scope.ServiceProvider.GetRequiredService(auditRegistry.Modules[0].ContextType);
            await scope.ServiceProvider.GetRequiredService<IAuditLogWriter>().AppendHashedAsync(
                viewerUserId, AuditActions.ActivityBodyViewed, "ApiRequestLogs", found.RequestId.ToString("D"), new { viewer = viewerUserId }, cancellationToken);
            await audit.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            scope.ServiceProvider.GetRequiredService<IAuditLogAccessor>().Clear();
            return AuditUnavailable();
        }

        return found;
    }

    public async Task<IReadOnlyList<ApiMutationIntent>> IncompleteIntentsAsync(int limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, 500);
        var cutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(options.IncompleteIntentAgeMinutes);
        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        return await IncompleteQuery(db, cutoff).OrderBy(i => i.Id).Take(take).ToListAsync(cancellationToken);
    }

    private static ChronoAuditError AuditUnavailable()
        => ChronoAuditError.Unavailable("ChronoAudit.AuditUnavailable", "The view could not be recorded, so nothing is returned; try again.");

    // Intents older than the cutoff with no request-log row of the same RequestId: one query with NOT EXISTS.
    private static IQueryable<ApiMutationIntent> IncompleteQuery(DbContext db, DateTimeOffset cutoff)
        => db.Set<ApiMutationIntent>().AsNoTracking()
            .Where(i => i.StartedAtUtc < cutoff && !db.Set<ApiRequestLog>().Any(l => l.RequestId == i.RequestId));

    private static string Cursor(long id) => CursorCodec.Encode(CursorPrefix, id.ToString(CultureInfo.InvariantCulture));

    private static ChronoAuditError? Validate(ActivitySearchQuery query, out long? before)
    {
        before = null;
        if (query.Limit < 1 || query.Limit > MaxLimit)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", $"Limit must be between 1 and {MaxLimit}.");
        }

        if (query.From > query.To)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The start of the range must not be after its end.");
        }

        var wide = query.UserId is not null || query.SessionId is not null || query.CorrelationId is not null;
        var maxDays = wide ? WideDays : NarrowDays;
        if (query.To - query.From > TimeSpan.FromDays(maxDays))
        {
            return ChronoAuditError.Validation(
                "ChronoAudit.Validation", $"The range may be at most {maxDays} days{(wide ? string.Empty : " unless a user, a session or a correlation id is given")}.");
        }

        if (query.StatusClass is not null && query.StatusClass is not ("2xx" or "3xx" or "4xx" or "5xx"))
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The status class must be 2xx, 3xx, 4xx or 5xx.");
        }

        if (query.Before is not null)
        {
            if (!CursorCodec.TryDecode(query.Before, CursorPrefix, out var text)
                || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return ChronoAuditError.Validation("ChronoAudit.InvalidCursor", "The cursor is not valid.");
            }

            before = id;
        }

        return null;
    }
}
