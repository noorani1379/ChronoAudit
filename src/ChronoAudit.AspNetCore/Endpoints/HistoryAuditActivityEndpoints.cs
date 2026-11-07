using System.Globalization;
using ChronoAudit;
using ChronoAudit.AspNetCore;
using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>The history, audit and activity endpoint groups of ChronoAudit.</summary>
public static class ChronoAuditHistoryAuditActivityEndpoints
{
    private const int MaxExportRows = 100_000;

    /// <summary>
    /// Maps the field-level history: <c>GET tables</c>, <c>GET {module}/tables/{table}/changes</c> and
    /// <c>GET {module}/tables/{table}/records/{key}/changes</c>. Personal fields are masked unless <c>reveal=true</c> is asked for, allowed
    /// by the host policy and recorded first.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditHistory(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/history")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet("tables", (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var history = http.RequestServices.GetRequiredService<IAuditHistory>();
            var modules = history.Modules.Select(m => new { module = m, tables = history.Find(m)?.Tables ?? [] }).ToList();
            return ChronoAuditHttpResults.Json(modules);
        });

        group.MapGet("{module}/tables/{table}/changes", async (HttpContext http, string module, string table) =>
        {
            var paging = Paging(http, "from", "to", "cursor", "limit", "reveal");
            if (paging.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, paging.Error);
            }

            var from = EndpointSupport.Date(http, "from");
            var to = EndpointSupport.Date(http, "to");
            if (from.IsFailure || to.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, (from.IsFailure ? (ChronoAuditResult)from : to).Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            var result = await http.RequestServices.GetRequiredService<IChangeReader>().TableAsync(
                module, table, scope, from.Value, to.Value, EndpointSupport.Text(http, "cursor"), paging.Value.Limit, paging.Value.Reveal, http.RequestAborted);
            return ChronoAuditHttpResults.Of(http, result);
        });

        group.MapGet("{module}/tables/{table}/records/{key}/changes", async (HttpContext http, string module, string table, string key) =>
        {
            var paging = Paging(http, "cursor", "limit", "reveal");
            if (paging.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, paging.Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            var result = await http.RequestServices.GetRequiredService<IChangeReader>().RecordAsync(
                module, table, key, scope, EndpointSupport.Text(http, "cursor"), paging.Value.Limit, paging.Value.Reveal, http.RequestAborted);
            return ChronoAuditHttpResults.Of(http, result);
        });

        return group;
    }

    private static ChronoAuditResult<(int Limit, bool Reveal)> Paging(HttpContext http, params string[] allowed)
    {
        if (EndpointSupport.RefuseUnknownKeys(http, allowed) is not null)
        {
            // The caller turns the error into the answer; the key is named in it.
            var key = http.Request.Query.Keys.First(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase));
            return FilterErrors.NotAllowed(key);
        }

        var limit = EndpointSupport.Int(http, "limit", 50);
        var reveal = EndpointSupport.Bool(http, "reveal");
        if (limit.IsFailure || reveal.IsFailure)
        {
            return (limit.IsFailure ? (ChronoAuditResult)limit : reveal).Error;
        }

        return (limit.Value, reveal.Value);
    }

    /// <summary>
    /// Maps the audit table routes: <c>GET {module}/search</c> and <c>POST verify</c> (re-walks a hash chain; the run is recorded before it
    /// starts). Both need the All data scope.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditAudit(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/audit")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet("{module}/search", async (HttpContext http, string module) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "from", "to", "actor", "resourceType", "resourceId", "action", "cursor", "limit") is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            if (EndpointSupport.RequireAll(http, scope) is { } denied)
            {
                return denied;
            }

            var from = EndpointSupport.Date(http, "from");
            var to = EndpointSupport.Date(http, "to");
            var limit = EndpointSupport.Int(http, "limit", 50);
            if (from.IsFailure || to.IsFailure || limit.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, (from.IsFailure ? from : to.IsFailure ? to : (ChronoAuditResult)limit).Error);
            }

            if (from.Value is null || to.Value is null)
            {
                return ChronoAuditHttpResults.Problem(http, AnalyticsErrors.RangeRequired);
            }

            var query = new AuditSearchQuery(
                module, from.Value.Value, to.Value.Value, EndpointSupport.Text(http, "actor"), EndpointSupport.Text(http, "resourceType"),
                EndpointSupport.Text(http, "resourceId"), EndpointSupport.Text(http, "action"),
                EndpointSupport.Text(http, "cursor"), limit.Value);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IAuditSearch>().SearchAsync(query, http.RequestAborted));
        });

        group.MapPost("verify", async (HttpContext http) =>
        {
            var scope = await EndpointSupport.ScopeAsync(http);
            if (EndpointSupport.RequireAll(http, scope) is { } denied)
            {
                return denied;
            }

            var body = await EndpointSupport.BodyAsync<VerifyRequest>(http);
            if (body.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, body.Error);
            }

            var request = body.Value;
            if (string.IsNullOrWhiteSpace(request.Module) || request.AfterChainIndex < 0 || request.MaxRows is < 1)
            {
                return ChronoAuditHttpResults.Problem(
                    http, ChronoAuditError.Validation("ChronoAudit.Validation", "A module is required; afterChainIndex must not be negative and maxRows must be at least 1."));
            }

            var source = http.RequestServices.GetRequiredService<IAuditChainSources>().Find(request.Module);
            if (source is null)
            {
                return ChronoAuditHttpResults.Problem(http, ChronoAuditError.NotFound("Chain.UnknownModule", "No audited module has this name."));
            }

            // The run is recorded before it starts; when it cannot be recorded it does not run.
            var recorded = await http.RequestServices.GetRequiredService<IAuditRecorder>().RecordAsync(
                scope.UserId ?? "unknown", AuditActions.ChainVerified, "AuditChain", source.Module, new { request.AfterChainIndex, request.MaxRows }, http.RequestAborted);
            if (recorded.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, recorded.Error);
            }

            var verification = await source.VerifyRangeAsync(request.AfterChainIndex ?? 0, request.MaxRows ?? int.MaxValue, http.RequestAborted);
            var anchors = http.RequestServices.GetService<AuditAnchorLog>()?.ReadAll().Anchors.Where(a => string.Equals(a.Module, source.Module, StringComparison.Ordinal)).ToList() ?? [];
            var anchorCheck = anchors.Count == 0 ? AnchorCheck.None : await source.VerifyAnchorsAsync(anchors, http.RequestAborted);
            return ChronoAuditHttpResults.Json(new
            {
                valid = verification.IsValid,
                verifiedCount = verification.VerifiedCount,
                pendingCount = verification.PendingCount,
                failedAtChainIndex = verification.FailedAtChainIndex,
                failedAtSequence = verification.FailedAtSequence,
                failure = verification.Failure?.ToString(),
                nextAfterChainIndex = verification.NextAfterChainIndex,
                anchorsChecked = anchorCheck.Checked,
                anchorMismatches = anchorCheck.Mismatches,
            });
        });

        return group;
    }

    private sealed record VerifyRequest(string? Module, long? AfterChainIndex, int? MaxRows);

    /// <summary>
    /// Maps the API activity log routes: <c>GET search</c>, <c>GET export.csv</c> (synchronous CSV, at most 100 000 rows, recorded before the
    /// first byte), <c>GET statistics</c>, <c>GET requests/{logId}/body</c> (recorded first) and <c>GET intents/incomplete</c>. A caller
    /// without the All scope sees only their own calls; the body and the incomplete intents need the All scope.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditActivity(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/activity")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet("search", async (HttpContext http) =>
        {
            var built = await ActivityQueryAsync(http, withPaging: true);
            if (built.Refusal is not null)
            {
                return built.Refusal;
            }

            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IActivitySearch>().SearchAsync(built.Query!, http.RequestAborted));
        });

        group.MapGet("export.csv", async (HttpContext http) =>
        {
            var built = await ActivityQueryAsync(http, withPaging: false);
            if (built.Refusal is not null)
            {
                return built.Refusal;
            }

            var search = http.RequestServices.GetRequiredService<IActivitySearch>();
            var query = built.Query! with { Limit = 200 };
            var counted = await search.CountAsync(query, MaxExportRows, http.RequestAborted);
            if (counted.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, counted.Error);
            }

            if (counted.Value > MaxExportRows)
            {
                return ChronoAuditHttpResults.Problem(
                    http, ChronoAuditError.Validation("Export.TooLarge", $"The export is over the limit of {MaxExportRows:N0} rows; narrow the period or the filter, or ask for a background export."));
            }

            var recorded = await http.RequestServices.GetRequiredService<IAuditRecorder>().RecordAsync(
                built.Scope!.UserId ?? "unknown", AuditActions.ActivityExported, "ApiRequestLogs", "csv", new { query.From, query.To, Rows = counted.Value }, http.RequestAborted);
            if (recorded.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, recorded.Error);
            }

            return new ActivityCsvResult(search, query);
        });

        group.MapGet("statistics", async (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "from", "to", "by") is { } refused)
            {
                return refused;
            }

            var from = EndpointSupport.Date(http, "from");
            var to = EndpointSupport.Date(http, "to");
            if (from.IsFailure || to.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, (from.IsFailure ? (ChronoAuditResult)from : to).Error);
            }

            if (from.Value is null || to.Value is null)
            {
                return ChronoAuditHttpResults.Problem(http, AnalyticsErrors.RangeRequired);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            var result = await http.RequestServices.GetRequiredService<IActivityStatistics>().CountAsync(
                from.Value.Value, to.Value.Value, EndpointSupport.Text(http, "by") ?? "day", scope, http.RequestAborted);
            return ChronoAuditHttpResults.Of(http, result);
        });

        group.MapGet("requests/{logId:long}/body", async (HttpContext http, long logId) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            if (EndpointSupport.RequireAll(http, scope) is { } denied)
            {
                return denied;
            }

            if (EndpointSupport.RequireUser(http, scope) is { } noUser)
            {
                return noUser;
            }

            var result = await http.RequestServices.GetRequiredService<IActivitySearch>().ViewBodyAsync(logId, scope.UserId!, http.RequestAborted);
            return ChronoAuditHttpResults.Of(http, result);
        });

        group.MapGet("intents/incomplete", async (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "limit") is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            if (EndpointSupport.RequireAll(http, scope) is { } denied)
            {
                return denied;
            }

            var limit = EndpointSupport.Int(http, "limit", 100);
            if (limit.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, limit.Error);
            }

            var intents = await http.RequestServices.GetRequiredService<IActivitySearch>().IncompleteIntentsAsync(limit.Value, http.RequestAborted);
            return ChronoAuditHttpResults.Json(intents.Select(i => new
            {
                i.Id, i.RequestId, i.StartedAtUtc, i.UserId, i.SessionId, i.CorrelationId, i.Method, i.Path, i.EndpointKey, i.BodySha256, i.BodyLength,
            }));
        });

        return group;
    }

    // The query of the search and the export, bound from the query string; a caller without the All scope is pinned to their own calls.
    private static async Task<(IResult? Refusal, ActivitySearchQuery? Query, ChronoAuditScope? Scope)> ActivityQueryAsync(HttpContext http, bool withPaging)
    {
        string[] keys = withPaging
            ? ["from", "to", "userId", "sessionId", "correlationId", "endpointKey", "method", "statusCode", "statusClass", "ip", "incompleteOnly", "before", "limit"]
            : ["from", "to", "userId", "sessionId", "correlationId", "endpointKey", "method", "statusCode", "statusClass", "ip"];
        if (EndpointSupport.RefuseUnknownKeys(http, keys) is { } refused)
        {
            return (refused, null, null);
        }

        var from = EndpointSupport.Date(http, "from");
        var to = EndpointSupport.Date(http, "to");
        var correlation = EndpointSupport.Guid(http, "correlationId");
        var status = EndpointSupport.Int(http, "statusCode", -1);
        var limit = EndpointSupport.Int(http, "limit", 50);
        var incomplete = EndpointSupport.Bool(http, "incompleteOnly");
        var failure = new ChronoAuditResult[] { from, to, correlation, status, limit, incomplete }.FirstOrDefault(r => r.IsFailure);
        if (failure is not null)
        {
            return (ChronoAuditHttpResults.Problem(http, failure.Error), null, null);
        }

        if (from.Value is null || to.Value is null)
        {
            // The export and the search both need the period (the search can still narrow by a user for a longer one).
            return (ChronoAuditHttpResults.Problem(http, AnalyticsErrors.RangeRequired), null, null);
        }

        var scope = await EndpointSupport.ScopeAsync(http);
        var userId = EndpointSupport.Text(http, "userId");
        if (scope.Kind != ScopeKind.All)
        {
            if (scope.UserId is null)
            {
                return (ChronoAuditHttpResults.Problem(http, ChronoAuditError.Forbidden("ChronoAudit.ScopeDenied", "A signed-in user is required.")), null, null);
            }

            if (userId is not null && !string.Equals(userId, scope.UserId, StringComparison.Ordinal))
            {
                return (ChronoAuditHttpResults.Problem(http, ChronoAuditError.Forbidden("ChronoAudit.ScopeDenied", "You can only see your own calls.")), null, null);
            }

            userId = scope.UserId;
        }

        var query = new ActivitySearchQuery(
            from.Value.Value, to.Value.Value, userId, EndpointSupport.Text(http, "sessionId"), correlation.Value,
            EndpointSupport.Text(http, "endpointKey"), EndpointSupport.Text(http, "method"), status.Value < 0 ? null : status.Value,
            EndpointSupport.Text(http, "statusClass"), EndpointSupport.Text(http, "ip"), incomplete.Value,
            EndpointSupport.Text(http, "before"), limit.Value);
        return (null, query, scope);
    }

    // The CSV of the activity log: written page by page after the run was recorded; the BOM first, like every ChronoAudit CSV.
    private sealed class ActivityCsvResult(IActivitySearch search, ActivitySearchQuery query) : IResult
    {
        private static readonly IReadOnlyList<CsvColumn<ActivityRow>> Columns =
        [
            new("Id", r => r.Id.ToString(CultureInfo.InvariantCulture), IsText: false),
            new("RequestId", r => r.RequestId.ToString("D")),
            new("OccurredAtUtc", r => CsvWriter.Instant(r.OccurredAtUtc), IsText: false),
            new("DurationMs", r => r.DurationMs.ToString(CultureInfo.InvariantCulture), IsText: false),
            new("UserId", r => r.UserId),
            new("SessionId", r => r.SessionId),
            new("CorrelationId", r => r.CorrelationId?.ToString("D")),
            new("Method", r => r.HttpMethod),
            new("Path", r => r.Path),
            new("EndpointKey", r => r.EndpointKey),
            new("StatusCode", r => r.StatusCode.ToString(CultureInfo.InvariantCulture), IsText: false),
            new("ErrorCode", r => r.ErrorCode),
            new("IpAddress", r => r.IpAddress),
        ];

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = "text/csv; charset=utf-8";
            response.Headers.ContentDisposition = $"attachment; filename=\"chronoaudit-activity-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv\"";
            var token = httpContext.RequestAborted;
            await response.Body.WriteAsync(CsvWriter.Bom.ToArray(), token);
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(CsvWriter.HeaderLine(Columns)), token);
            string? cursor = null;
            do
            {
                var page = await search.SearchAsync(query with { Before = cursor }, token);
                if (page.IsFailure)
                {
                    // The headers are gone: the stream ends here and the client sees a short file.
                    return;
                }

                var text = new StringBuilder();
                foreach (var row in page.Value.Items)
                {
                    text.Append(CsvWriter.Line(row, Columns));
                }

                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), token);
                cursor = page.Value.NextCursor;
            }
            while (cursor is not null);
        }
    }
}
