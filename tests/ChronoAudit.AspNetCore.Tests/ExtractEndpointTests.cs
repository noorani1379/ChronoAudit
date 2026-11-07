using System.Net;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class ExtractEndpointTests
{
    private sealed class NoAuditRecorder : IAuditRecorder
    {
        public Task<ChronoAuditResult> RecordAsync(
            string actorUserId, string action, string resourceType, string resourceId, object? metadata, CancellationToken cancellationToken)
            => Task.FromResult(ChronoAuditResult.Failure(ChronoAuditError.Unavailable("ChronoAudit.AuditUnavailable", "audit recorder down")));
    }

    private sealed class FailingExtractReader(IExtractReader inner) : IExtractReader
    {
        private int _calls;

        public IReadOnlyList<ExtractColumn> Columns => inner.Columns;

        public Task<ChronoAuditResult<ExtractBatch>> NextAsync(ExtractQuery query, int maxRows, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls > 1)
            {
                throw new InvalidOperationException("stream cut midway");
            }

            return inner.NextAsync(query, maxRows, cancellationToken);
        }
    }

    [Fact]
    public async Task TheExtract_StreamsCsvWithBom_AndRecordsStartAndFinish()
    {
        await using var rig = await HttpRig.StartAsync();
        await Seed.OrdersAsync(rig, 3);

        var response = await rig.GetAsync($"/chronoaudit/extract/csv?{Seed.Range}", Who.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.StartsWith("attachment; filename=\"chronoaudit-extract-", response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.True(bytes.Length > 3);

        var auditRows = await rig.AuditRowsAsync();
        Assert.Contains(auditRows, a => a.Action == AuditActions.ExtractRun && a.ActorUserId == "admin");
        Assert.Contains(auditRows, a => a.Action == AuditActions.ExtractFinished && a.ActorUserId == "admin");
    }

    [Fact]
    public async Task WhenTheRunCannotBeRecorded_NothingIsWritten_AndTheResponseIsNotStarted()
    {
        await using var rig = await HttpRig.StartAsync(services: s => s.AddSingleton<IAuditRecorder>(new NoAuditRecorder()));
        await Seed.OrdersAsync(rig, 1);

        var response = await rig.GetAsync($"/chronoaudit/extract/csv?{Seed.Range}", Who.Admin);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = await HttpRig.JsonAsync(response);
        Assert.Equal("ChronoAudit.AuditUnavailable", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ACutRun_StillLeavesAFinishedRecord_WithHowFarItGot()
    {
        await using var rig = await HttpRig.StartAsync(services: s =>
        {
            var descriptor = s.First(d => d.ServiceType == typeof(IExtractReader));
            s.Remove(descriptor);
            s.AddSingleton<IExtractReader>(sp => new FailingExtractReader(
                ActivatorUtilities.CreateInstance<ExtractReader>(sp)));
        });
        await Seed.OrdersAsync(rig, 3);

        try
        {
            var response = await rig.GetAsync($"/chronoaudit/extract/csv?{Seed.Range}", Who.Admin);
            await response.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            // The cut stream is expected to throw or truncate.
        }

        var auditRows = await rig.AuditRowsAsync();
        var finished = Assert.Single(auditRows, a => a.Action == AuditActions.ExtractFinished);
        Assert.Contains("\"Completed\":false", finished.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOwnScopeCaller_OrNoRange_IsRefused()
    {
        await using var rig = await HttpRig.StartAsync();

        var own = await rig.GetAsync($"/chronoaudit/extract/csv?{Seed.Range}", Who.Me());
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        using var ownBody = await HttpRig.JsonAsync(own);
        Assert.Equal("ChronoAudit.ScopeDenied", ownBody.RootElement.GetProperty("title").GetString());

        var noRange = await rig.GetAsync("/chronoaudit/extract/csv", Who.Admin);
        Assert.Equal(HttpStatusCode.BadRequest, noRange.StatusCode);
        using var noRangeBody = await HttpRig.JsonAsync(noRange);
        Assert.Equal("Analytics.RangeRequired", noRangeBody.RootElement.GetProperty("title").GetString());
    }
}
