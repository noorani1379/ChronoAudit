using System.Text.Json;
using ChronoAudit;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>The analytics, saved report and export endpoint groups of ChronoAudit.</summary>
public static class ChronoAuditAnalyticsReportsExportsEndpoints
{
    /// <summary>Maps the analytics routes: <c>GET catalog</c> (what the caller may run) and <c>POST run</c> (body: an analytics query).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditAnalytics(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/analytics")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet("catalog", async (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Json(http.RequestServices.GetRequiredService<IAnalyticsService>().Catalog(scope));
        });

        group.MapPost("run", async (HttpContext http) =>
        {
            var body = await EndpointSupport.BodyAsync<AnalyticsQuery>(http);
            if (body.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, body.Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IAnalyticsService>().RunAsync(body.Value, scope, http.RequestAborted));
        });

        return group;
    }

    /// <summary>
    /// Maps the saved report routes: <c>GET</c> (list), <c>POST</c> (create), <c>GET {id}</c>, <c>PUT {id}</c> (body: the input and an
    /// optional <c>expectedStamp</c>), <c>DELETE {id}</c> (optional <c>?expectedStamp=</c>) and <c>POST {id}/run</c> (body: an optional new period).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditSavedReports(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/saved-reports")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet(string.Empty, async (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "limit") is { } refused)
            {
                return refused;
            }

            var limit = EndpointSupport.Int(http, "limit", 50);
            if (limit.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, limit.Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<ISavedReportService>().ListAsync(scope, limit.Value, http.RequestAborted));
        });

        group.MapPost(string.Empty, async (HttpContext http) =>
        {
            var body = await EndpointSupport.BodyAsync<SavedReportInput>(http);
            if (body.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, body.Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            var result = await http.RequestServices.GetRequiredService<ISavedReportService>().CreateAsync(body.Value, scope, http.RequestAborted);
            return result.IsSuccess
                ? Results.Json(result.Value, ChronoAuditJson.Options, statusCode: StatusCodes.Status201Created)
                : ChronoAuditHttpResults.Problem(http, result.Error);
        });

        group.MapGet("{id:guid}", async (HttpContext http, Guid id) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<ISavedReportService>().GetAsync(id, scope, http.RequestAborted));
        });

        group.MapPut("{id:guid}", async (HttpContext http, Guid id) =>
        {
            var body = await EndpointSupport.BodyAsync<SavedReportUpdate>(http);
            if (body.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, body.Error);
            }

            var update = body.Value;
            if (update.Definition is null || update.Title is null)
            {
                return ChronoAuditHttpResults.Problem(http, ChronoAuditError.Validation("SavedReport.DefinitionInvalid", "A title and a definition are required."));
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(
                http,
                await http.RequestServices.GetRequiredService<ISavedReportService>().UpdateAsync(
                    id, new SavedReportInput(update.Title, update.Definition, update.SharedWithGroupId), update.ExpectedStamp, scope, http.RequestAborted));
        });

        group.MapDelete("{id:guid}", async (HttpContext http, Guid id) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "expectedStamp") is { } refused)
            {
                return refused;
            }

            var stamp = EndpointSupport.Guid(http, "expectedStamp");
            if (stamp.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, stamp.Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<ISavedReportService>().DeleteAsync(id, stamp.Value, scope, http.RequestAborted));
        });

        group.MapPost("{id:guid}/run", async (HttpContext http, Guid id) =>
        {
            var period = new SavedReportPeriod(null, null);
            if (http.Request.ContentLength is > 0)
            {
                var body = await EndpointSupport.BodyAsync<SavedReportPeriod>(http);
                if (body.IsFailure)
                {
                    return ChronoAuditHttpResults.Problem(http, body.Error);
                }

                period = body.Value;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(
                http, await http.RequestServices.GetRequiredService<ISavedReportService>().RunAsync(id, period.From, period.To, scope, http.RequestAborted));
        });

        return group;
    }

    private sealed record SavedReportUpdate(string? Title, AnalyticsQuery? Definition, string? SharedWithGroupId, Guid? ExpectedStamp);

    private sealed record SavedReportPeriod(DateTimeOffset? From, DateTimeOffset? To);

    /// <summary>
    /// Maps the background export routes: <c>POST</c> (request), <c>GET</c> (list), <c>GET {id}</c>, <c>POST {id}/cancel</c> and
    /// <c>GET {id}/download</c> (the file, with the header <c>X-Content-SHA256</c>). A job is private to the user who asked for it.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditExports(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/exports")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapPost(string.Empty, async (HttpContext http) =>
        {
            var body = await EndpointSupport.BodyAsync<ExportRequestBody>(http);
            if (body.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, body.Error);
            }

            var request = body.Value;
            if (string.IsNullOrWhiteSpace(request.Producer))
            {
                return ChronoAuditHttpResults.Problem(http, ChronoAuditError.Validation("Export.UnknownProducer", "A producer key is required."));
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            var result = await http.RequestServices.GetRequiredService<IExportService>().RequestAsync(
                new ExportRequest(request.Producer, request.Format ?? ExportFormat.Csv, request.Parameters ?? EmptyObject), scope, http.RequestAborted);
            return result.IsSuccess
                ? Results.Json(result.Value, ChronoAuditJson.Options, statusCode: StatusCodes.Status201Created)
                : ChronoAuditHttpResults.Problem(http, result.Error);
        });

        group.MapGet(string.Empty, async (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "limit") is { } refused)
            {
                return refused;
            }

            var limit = EndpointSupport.Int(http, "limit", 20);
            if (limit.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, limit.Error);
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IExportService>().ListAsync(scope, limit.Value, http.RequestAborted));
        });

        group.MapGet("{id:guid}", async (HttpContext http, Guid id) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IExportService>().GetAsync(id, scope, http.RequestAborted));
        });

        group.MapPost("{id:guid}/cancel", async (HttpContext http, Guid id) =>
        {
            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IExportService>().CancelAsync(id, scope, http.RequestAborted));
        });

        group.MapGet("{id:guid}/download", async (HttpContext http, Guid id) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            var result = await http.RequestServices.GetRequiredService<IExportService>().DownloadAsync(id, scope, http.RequestAborted);
            if (result.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, result.Error);
            }

            var download = result.Value;
            http.Response.Headers[ChronoAuditHttpHeaders.ContentSha256] = download.Sha256;
            return Results.File(download.Content, download.ContentType, download.DownloadName, enableRangeProcessing: false);
        });

        return group;
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private sealed record ExportRequestBody(string? Producer, ExportFormat? Format, JsonElement? Parameters);
}
