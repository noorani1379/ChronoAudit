using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class TimelineAnalyticsTests
{
    private static readonly DateTimeOffset T0 = TimelineRig.T0;

    private static async Task<ActivityHost> HostAsync()
    {
        var host = await TimelineRig.HostAsync();
        foreach (var e in new[]
        {
            TimelineRig.Event(1, type: "order.created", at: T0, actor: "alice", subjectId: "o-1"),
            TimelineRig.Event(2, type: "order.paid", at: T0.AddMinutes(1), actor: "alice", subjectId: "o-1"),
            TimelineRig.Event(3, type: "order.paid", at: T0.AddMinutes(2), actor: "bob", subjectId: "o-2"),
            TimelineRig.Event(4, type: "invoice.sent", subjectType: "Invoice", subjectId: "i-1", at: T0.AddDays(40), actor: "bob"),
        })
        {
            await TimelineRig.Writer(host).RecordAsync(e, CancellationToken.None);
        }

        return host;
    }

    private static AnalyticsQuery Query(string dimension, string measure = "count", IReadOnlyList<FilterNode>? filters = null)
        => new("timeline", [dimension], [measure], filters, T0.AddDays(-1), T0.AddDays(60), SortBy: dimension, SortDescending: false);

    [Fact]
    public async Task TheTimelineSubject_IsInTheCatalog_OnlyWhenTheTimelineWasAdded()
    {
        await using var with = await HostAsync();
        await using var without = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());

        var entry = Assert.Single(with.Provider.GetRequiredService<IAnalyticsService>().Catalog(ChronoAuditScope.OwnOf("alice")), c => c.Key == "timeline");

        Assert.Equal(new[] { "eventType", "subjectType", "actorRole", "day", "month", "quarter", "year" }, entry.Dimensions.Select(d => d.Key));
        Assert.Equal(new[] { "count", "distinctSubjects", "distinctActors" }, entry.Measures.Select(m => m.Key));
        Assert.Equal(new[] { "eventType", "subjectType", "actor", "groupKey" }, entry.Filters.Select(f => f.Key));
        Assert.DoesNotContain(without.Provider.GetRequiredService<IAnalyticsService>().Catalog(ChronoAuditScope.Everything(null)), c => c.Key == "timeline");
    }

    [SqliteDecimalFact]
    public async Task EveryDimensionAndMeasure_RunsOnSqlite()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();
        var entry = service.Catalog(ChronoAuditScope.Everything(null)).Single(c => c.Key == "timeline");

        foreach (var dimension in entry.Dimensions)
        {
            var result = await service.RunAsync(
                new AnalyticsQuery("timeline", [dimension.Key], entry.Measures.Select(m => m.Key).ToList(), null, T0.AddDays(-1), T0.AddDays(60)), ChronoAuditScope.Everything(null), CancellationToken.None);
            Assert.True(result.IsSuccess, dimension.Key + ": " + (result.IsFailure ? result.Error.Message : string.Empty));
        }
    }

    [SqliteDecimalFact]
    public async Task TheCountsAreGrouped_ByEventType_ByMonthOfTheStoredCalendarDate_AndFiltered()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();
        var all = ChronoAuditScope.Everything("admin");

        var byType = await service.RunAsync(Query("eventType"), all, CancellationToken.None);
        var byMonth = await service.RunAsync(Query("month"), all, CancellationToken.None);
        var filtered = await service.RunAsync(Query("eventType", "distinctActors", [FilterNode.Condition("eventType", FilterOp.Prefix, "order.")]), all, CancellationToken.None);
        var subjects = await service.RunAsync(Query("eventType", "distinctSubjects", [FilterNode.Condition("actor", FilterOp.Eq, "alice")]), all, CancellationToken.None);

        Assert.Equal(new[] { ("invoice.sent", 1m), ("order.created", 1m), ("order.paid", 2m) }, byType.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0]!.Value)));
        Assert.Equal(new[] { ("2026/11", 3m), ("2026/12", 1m) }, byMonth.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0]!.Value)));
        Assert.Equal(new[] { ("order.created", 1m), ("order.paid", 2m) }, filtered.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0]!.Value)));
        Assert.Equal(new[] { ("order.created", 1m), ("order.paid", 1m) }, subjects.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0]!.Value)));
    }

    [SqliteDecimalFact]
    public async Task TheScope_IsAppliedInsideTheSubjectQuery()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();

        var alice = await service.RunAsync(Query("eventType"), ChronoAuditScope.OwnOf("alice"), CancellationToken.None);
        var nobody = await service.RunAsync(Query("eventType"), ChronoAuditScope.OwnOf("nobody"), CancellationToken.None);

        Assert.Equal(new[] { ("order.created", 1m), ("order.paid", 1m) }, alice.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0]!.Value)));
        Assert.Empty(nobody.Value.Rows);
    }
}

public sealed class TimelineExportTests
{
    private static readonly DateTimeOffset T0 = TimelineRig.T0;

    private static string Parameters(string extra = "") => $"{{\"from\":\"{T0.AddDays(-1):O}\",\"to\":\"{T0.AddDays(30):O}\"{extra}}}";

    private static async Task<ExportRig> RigAsync()
    {
        var rig = await ExportRig.CreateAsync(more: b => b.AddTimeline(), configure: null);
        rig.Host.Clock.Set(T0);
        return rig;
    }

    private static async Task<string> TextOfAsync(ExportRig rig, string parameters, ChronoAuditScope scope)
    {
        var dto = await rig.RequestAsync("timeline", ExportFormat.Csv, parameters, scope);
        await rig.DrainAsync();
        var job = await rig.JobAsync(dto.Id);
        Assert.True(job.State == ExportState.Completed, job.ErrorCode + ": " + job.ErrorMessage);
        var bytes = await File.ReadAllBytesAsync(rig.PathOf(job));
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    [Fact]
    public async Task TheTimelineProducer_WritesTheEventsNewestFirst_InsideTheScope_WithTheMaskedPayload()
    {
        await using var rig = await ExportRig.CreateAsync(more: b => b.AddTimeline(), configure: null);
        var writer = rig.Host.Provider.GetRequiredService<ITimelineWriter>();
        await writer.RecordAsync(TimelineRig.Event(1, at: T0, actor: "alice", payload: """{"password":"s3cret","n":1}"""), CancellationToken.None);
        await writer.RecordAsync(TimelineRig.Event(2, at: T0.AddMinutes(5), actor: "bob"), CancellationToken.None);
        await writer.RecordAsync(TimelineRig.Event(3, at: T0.AddMinutes(9), actor: "alice", type: "order.shipped"), CancellationToken.None);

        var mine = await TextOfAsync(rig, Parameters(), ChronoAuditScope.OwnOf("alice"));
        var everything = await TextOfAsync(rig, Parameters(), ChronoAuditScope.Everything("admin"));

        var lines = everything.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("OccurredAtUtc,EventType,Title,SubjectType,SubjectId,GroupKey,ActorDisplayName,ActorRole,Payload", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length - 1);
        Assert.Contains("order.shipped", lines[1], StringComparison.Ordinal);
        Assert.Equal(2, mine.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.DoesNotContain("s3cret", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTimelineProducer_FiltersBySubjectAndGroup_AndRefusesBadParameters()
    {
        await using var rig = await RigAsync();
        var writer = rig.Host.Provider.GetRequiredService<ITimelineWriter>();
        await writer.RecordAsync(TimelineRig.Event(1, subjectId: "o-1", group: "case-1"), CancellationToken.None);
        await writer.RecordAsync(TimelineRig.Event(2, subjectId: "o-2"), CancellationToken.None);
        var all = ChronoAuditScope.Everything("admin");

        var subject = await TextOfAsync(rig, Parameters(",\"subjectType\":\"Order\",\"subjectId\":\"o-2\""), all);
        var group = await TextOfAsync(rig, Parameters(",\"groupKey\":\"case-1\""), all);
        var idWithoutType = await rig.Service.RequestAsync(new ExportRequest("timeline", ExportFormat.Csv, ExportRig.Json(Parameters(",\"subjectId\":\"o-2\""))), all, CancellationToken.None);
        var tooWide = await rig.Service.RequestAsync(
            new ExportRequest("timeline", ExportFormat.Csv, ExportRig.Json("{\"from\":\"2025-01-01T00:00:00Z\",\"to\":\"2026-12-01T00:00:00Z\"}")), all, CancellationToken.None);

        Assert.Equal(1, subject.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Contains("o-2", subject, StringComparison.Ordinal);
        Assert.Equal(1, group.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Equal("ChronoAudit.Validation", idWithoutType.Error.Code);
        Assert.Equal("ChronoAudit.Validation", tooWide.Error.Code);
    }

    [Fact]
    public async Task WithoutTheTimeline_TheProducerIsRefused()
    {
        await using var rig = await ExportRig.CreateAsync();

        var result = await rig.Service.RequestAsync(new ExportRequest("timeline", ExportFormat.Csv, ExportRig.Json(Parameters())), ExportRig.Admin, CancellationToken.None);

        Assert.Equal("Export.UnknownProducer", result.Error.Code);
    }

    [Fact]
    public async Task ManyEvents_AreExportedOnceEach_AcrossBatches_EvenWithEqualTimes()
    {
        await using var rig = await ExportRig.CreateAsync(more: b => b.AddTimeline(), configure: o => o.BatchSize = 100);
        rig.Host.Clock.Set(T0);
        var writer = rig.Host.Provider.GetRequiredService<ITimelineWriter>();
        const int total = 230;
        for (var i = 1; i <= total; i++)
        {
            await writer.RecordAsync(TimelineRig.Event(i, subjectId: "o-" + i, at: T0.AddMinutes(i % 3)), CancellationToken.None);
        }

        var text = await TextOfAsync(rig, Parameters(), ChronoAuditScope.Everything("admin"));

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.Equal(total, lines.Count);
        Assert.Equal(total, lines.Distinct().Count());
    }
}

public sealed class TimelineExtractTests
{
    private static readonly DateTimeOffset T0 = TimelineRig.T0;

    [Fact]
    public async Task TimelineEvents_ComeFirst_ThenTheModules()
    {
        await using var host = await TimelineRig.HostAsync();
        for (var i = 0; i < 2; i++)
        {
            using var scope = host.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.Orders.Add(new Order { Title = "order-" + i });
            await db.SaveChangesAsync();
        }

        await TimelineRig.Writer(host).RecordAsync(TimelineRig.Event(1, at: host.Clock.GetUtcNow(), payload: """{"password":"s3cret"}"""), CancellationToken.None);
        await TimelineRig.Writer(host).RecordAsync(TimelineRig.Event(2, at: host.Clock.GetUtcNow().AddMinutes(1)), CancellationToken.None);
        var reader = host.Provider.GetRequiredService<IExtractReader>();
        var query = new ExtractQuery(T0.AddDays(-1), T0.AddDays(1), IncludeTimeline: true);

        var rows = new List<ExtractRow>();
        for (var batch = await reader.NextAsync(query, 1, CancellationToken.None); ; batch = await reader.NextAsync(query, 1, CancellationToken.None))
        {
            Assert.True(batch.IsSuccess, batch.IsFailure ? batch.Error.Message : null);
            rows.AddRange(batch.Value.Rows);
            if (batch.Value.Done)
            {
                break;
            }

            query = query with { Cursor = batch.Value.NextCursor };
        }

        Assert.Equal(new[] { "timeline", "timeline", "shop", "shop" }, rows.Select(r => r.Module));
        Assert.Equal(new[] { "order.paid", "order.paid" }, rows.Take(2).Select(r => r.Action));
        Assert.Equal(new[] { "Order", "Order" }, rows.Take(2).Select(r => r.ResourceType));
        Assert.DoesNotContain("s3cret", rows[0].NewValues, StringComparison.Ordinal);
        Assert.Equal(rows.Count, rows.Select(r => (r.Module, r.Sequence)).Distinct().Count());
    }

    [Fact]
    public async Task WithoutTheFlag_NoTimelineRowsAreRead_AndWithoutTheTimelineTheFlagIsRefused()
    {
        await using var with = await TimelineRig.HostAsync();
        await using var without = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());
        await TimelineRig.Writer(with).RecordAsync(TimelineRig.Event(1, at: with.Clock.GetUtcNow()), CancellationToken.None);
        var query = new ExtractQuery(T0.AddDays(-1), T0.AddDays(1));

        var plain = await with.Provider.GetRequiredService<IExtractReader>().NextAsync(query, 10, CancellationToken.None);
        var refused = await without.Provider.GetRequiredService<IExtractReader>().NextAsync(query with { IncludeTimeline = true }, 10, CancellationToken.None);

        Assert.DoesNotContain(plain.Value.Rows, r => r.Module == "timeline");
        Assert.Equal("ChronoAudit.Validation", refused.Error.Code);
    }
}
