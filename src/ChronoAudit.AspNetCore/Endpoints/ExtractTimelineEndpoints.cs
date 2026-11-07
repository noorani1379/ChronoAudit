using ChronoAudit;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>The extract, timeline and all-in-one endpoint mapping of ChronoAudit.</summary>
public static class ChronoAuditExtractTimelineEndpoints
{
    /// <summary>
    /// Maps <c>GET csv</c>: the streaming CSV extract of all changes (and, with <c>includeTimeline=true</c>, the timeline events first). It
    /// needs the All scope. A bad call gets its answer before any byte; the run is recorded before the first byte, and a second row records
    /// how far it got (rows, size and SHA-256, and whether it completed) even when the client cut it.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditExtract(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/extract")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet("csv", async (HttpContext http) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http, "from", "to", "modules", "resourceType", "actor", "includeTimeline", "cursor") is { } refused)
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

            var from = EndpointSupport.Date(http, "from");
            var to = EndpointSupport.Date(http, "to");
            var includeTimeline = EndpointSupport.Bool(http, "includeTimeline");
            if (from.IsFailure || to.IsFailure || includeTimeline.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, (from.IsFailure ? (ChronoAuditResult)from : to.IsFailure ? to : includeTimeline).Error);
            }

            if (from.Value is null || to.Value is null)
            {
                return ChronoAuditHttpResults.Problem(http, AnalyticsErrors.RangeRequired);
            }

            var modules = EndpointSupport.Text(http, "modules")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var query = new ExtractQuery(
                from.Value.Value, to.Value.Value, modules, EndpointSupport.Text(http, "resourceType"), EndpointSupport.Text(http, "actor"),
                EndpointSupport.Text(http, "cursor"), null, includeTimeline.Value);

            // Validate first: a bad call gets its answer before any byte is written.
            var reader = http.RequestServices.GetRequiredService<IExtractReader>();
            var probe = await reader.NextAsync(query, 1, http.RequestAborted);
            if (probe.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, probe.Error);
            }

            var recorder = http.RequestServices.GetRequiredService<IAuditRecorder>();
            var recorded = await recorder.RecordAsync(
                scope.UserId!, AuditActions.ExtractRun, "Extract", "csv",
                new { query.From, query.To, query.Modules, query.ResourceType, query.ActorUserId, query.IncludeTimeline }, http.RequestAborted);
            if (recorded.IsFailure)
            {
                return ChronoAuditHttpResults.Problem(http, recorded.Error);
            }

            return new ExtractCsvResult(reader, recorder, query, scope.UserId!);
        });

        return group;
    }

    private sealed class ExtractCsvResult(IExtractReader reader, IAuditRecorder recorder, ExtractQuery query, string userId) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.ContentType = "text/csv; charset=utf-8";
            response.Headers.ContentDisposition = $"attachment; filename=\"chronoaudit-extract-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv\"";
            await using var output = new CountingStream(response.Body);
            long rows = 0;
            string? sha = null;
            var completed = false;
            try
            {
                (rows, sha) = await ExtractCsv.WriteAsync(reader, query, output, httpContext.RequestAborted);
                completed = true;
            }
            finally
            {
                // A cut run still leaves a finished record, with how far it got.
                await recorder.RecordAsync(
                    userId, AuditActions.ExtractFinished, "Extract", "csv", new { Completed = completed, Rows = rows, Bytes = output.Written, Sha256 = sha }, CancellationToken.None);
            }
        }
    }

    // Counts the bytes that left, without owning the response stream.
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Written += buffer.Length;
        }
    }

    /// <summary>
    /// Maps the timeline routes: <c>GET subjects/{subjectType}/{subjectId}</c>, <c>GET groups/{groupKey}</c>, <c>GET actors/{actorUserId}</c>
    /// (query: <c>eventTypes</c> comma separated, <c>from</c>, <c>to</c>, <c>before</c>, <c>limit</c>) and
    /// <c>GET subjects/{subjectType}/{subjectId}/evidence</c> (the evidence bundle; recorded first; All scope only).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The route group.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static RouteGroupBuilder MapChronoAuditTimeline(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit/timeline")
    {
        var group = EndpointSupport.NewGroup(endpoints, authorizationPolicy, pattern);

        group.MapGet("subjects/{subjectType}/{subjectId}/evidence", async (HttpContext http, string subjectType, string subjectId) =>
        {
            if (EndpointSupport.RefuseUnknownKeys(http) is { } refused)
            {
                return refused;
            }

            var scope = await EndpointSupport.ScopeAsync(http);
            return ChronoAuditHttpResults.Of(http, await http.RequestServices.GetRequiredService<IEvidenceService>().BuildAsync(subjectType, subjectId, scope, http.RequestAborted));
        });

        group.MapGet("subjects/{subjectType}/{subjectId}", (HttpContext http, string subjectType, string subjectId)
            => TimelineAsync(http, (reader, filter, scope) => reader.OfSubjectAsync(subjectType, subjectId, filter, scope, http.RequestAborted)));

        group.MapGet("groups/{groupKey}", (HttpContext http, string groupKey)
            => TimelineAsync(http, (reader, filter, scope) => reader.OfGroupAsync(groupKey, filter, scope, http.RequestAborted)));

        group.MapGet("actors/{actorUserId}", (HttpContext http, string actorUserId)
            => TimelineAsync(http, (reader, filter, scope) => reader.OfActorAsync(actorUserId, filter, scope, http.RequestAborted)));

        return group;
    }

    private static async Task<IResult> TimelineAsync(
        HttpContext http, Func<ITimelineReader, TimelineFilter, ChronoAuditScope, Task<ChronoAuditResult<TimelinePage>>> read)
    {
        if (EndpointSupport.RefuseUnknownKeys(http, "eventTypes", "from", "to", "before", "limit") is { } refused)
        {
            return refused;
        }

        var from = EndpointSupport.Date(http, "from");
        var to = EndpointSupport.Date(http, "to");
        var limit = EndpointSupport.Int(http, "limit", 50);
        if (from.IsFailure || to.IsFailure || limit.IsFailure)
        {
            return ChronoAuditHttpResults.Problem(http, (from.IsFailure ? (ChronoAuditResult)from : to.IsFailure ? to : limit).Error);
        }

        var types = EndpointSupport.Text(http, "eventTypes")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filter = new TimelineFilter(types, from.Value, to.Value, EndpointSupport.Text(http, "before"), limit.Value);
        var scope = await EndpointSupport.ScopeAsync(http);
        return ChronoAuditHttpResults.Of(http, await read(http.RequestServices.GetRequiredService<ITimelineReader>(), filter, scope));
    }

    /// <summary>
    /// Maps every ChronoAudit group under one base route and one policy: <c>history</c>, <c>audit</c>, <c>activity</c>, <c>analytics</c>,
    /// <c>saved-reports</c>, <c>exports</c>, <c>extract</c> and <c>timeline</c>. The features behind them must be registered (a route of a
    /// feature that was not added answers 500); to map only some, call the groups one by one.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="authorizationPolicy">The authorization policy every route requires (mandatory).</param>
    /// <param name="pattern">The base route.</param>
    /// <returns>The endpoint route builder.</returns>
    /// <exception cref="ArgumentException">The policy is null or blank.</exception>
    public static IEndpointRouteBuilder MapChronoAudit(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern = "chronoaudit")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
        {
            throw new ArgumentException("An authorization policy is required: the ChronoAudit endpoints are never mapped without one.", nameof(authorizationPolicy));
        }

        var root = pattern.Trim('/');
        endpoints.MapChronoAuditHistory(authorizationPolicy, root + "/history");
        endpoints.MapChronoAuditAudit(authorizationPolicy, root + "/audit");
        endpoints.MapChronoAuditActivity(authorizationPolicy, root + "/activity");
        endpoints.MapChronoAuditAnalytics(authorizationPolicy, root + "/analytics");
        endpoints.MapChronoAuditSavedReports(authorizationPolicy, root + "/saved-reports");
        endpoints.MapChronoAuditExports(authorizationPolicy, root + "/exports");
        endpoints.MapChronoAuditExtract(authorizationPolicy, root + "/extract");
        endpoints.MapChronoAuditTimeline(authorizationPolicy, root + "/timeline");
        return endpoints;
    }
}
