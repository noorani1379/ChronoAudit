using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class ExportServiceTests
{
    [Fact]
    public async Task ARequest_QueuesAJobWithTheScopeSnapshot_AndNothingRunsInTheRequest()
    {
        await using var rig = await ExportRig.CreateAsync();

        var dto = await rig.RequestAsync(scope: ChronoAuditScope.GroupOf("u1", "g1"), parameters: """{"note":"x"}""");

        var job = await rig.JobAsync(dto.Id);
        Assert.Equal((ExportState.Pending, "scripted", ExportFormat.Csv), (dto.State, dto.Producer, dto.Format));
        Assert.Equal(("u1", ScopeKind.Group, "g1", """{"note":"x"}"""), (job.RequestedByUserId, job.ScopeKind, job.ScopeGroupId, job.ParametersJson));
        Assert.Equal(0, rig.Script.Calls);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.Empty(await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task AtMostNActiveJobsPerUser_AFinishedOneDoesNotCount()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.MaxActivePerUser = 2);
        var first = await rig.RequestAsync();
        rig.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.RequestAsync();
        rig.Host.Clock.Advance(TimeSpan.FromSeconds(1));

        var third = await rig.Service.RequestAsync(new ExportRequest("scripted", ExportFormat.Csv, ExportRig.Json("{}")), ExportRig.Me, CancellationToken.None);
        var otherUser = await rig.Service.RequestAsync(new ExportRequest("scripted", ExportFormat.Csv, ExportRig.Json("{}")), ChronoAuditScope.OwnOf("u2"), CancellationToken.None);

        Assert.Equal(("Export.TooManyActive", ChronoAuditErrorKind.Conflict), (third.Error.Code, third.Error.Kind));
        Assert.True(otherUser.IsSuccess);
        using (var scope = rig.Host.Provider.CreateScope())
        {
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<StoreDb>().Set<ExportJob>().CountAsync());
        }

        await rig.Worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ExportState.Completed, (await rig.JobAsync(first.Id)).State);
        Assert.True((await rig.Service.RequestAsync(new ExportRequest("scripted", ExportFormat.Csv, ExportRig.Json("{}")), ExportRig.Me, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task AJobIsPrivateToItsRequester_ForReadCancelAndDownload()
    {
        await using var rig = await ExportRig.CreateAsync();
        var dto = await rig.RequestAsync();
        await rig.DrainAsync();
        var stranger = ChronoAuditScope.OwnOf("u2");

        foreach (var scope in new[] { stranger, ExportRig.Admin })
        {
            var read = await rig.Service.GetAsync(dto.Id, scope, CancellationToken.None);
            var cancel = await rig.Service.CancelAsync(dto.Id, scope, CancellationToken.None);
            var download = await rig.Service.DownloadAsync(dto.Id, scope, CancellationToken.None);

            Assert.Equal(("Export.NotFound", ChronoAuditErrorKind.NotFound), (read.Error.Code, read.Error.Kind));
            Assert.Equal("Export.NotFound", cancel.Error.Code);
            Assert.Equal("Export.NotFound", download.Error.Code);
            Assert.Empty((await rig.Service.ListAsync(scope, 50, CancellationToken.None)).Value);
        }

        Assert.Single((await rig.Service.ListAsync(ExportRig.Me, 50, CancellationToken.None)).Value);
        Assert.DoesNotContain(AuditActions.ExportDownloaded, await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task ADownload_NeedsAFinishedJob_ARecordOfTheDownload_AndTheFileItself()
    {
        await using var rig = await ExportRig.CreateAsync();
        var dto = await rig.RequestAsync();

        var early = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);
        await rig.DrainAsync();
        var download = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);

        Assert.Equal(("Export.NotReady", ChronoAuditErrorKind.Conflict), (early.Error.Code, early.Error.Kind));
        await using var content = download.Value.Content;
        var bytes = new MemoryStream();
        await content.CopyToAsync(bytes);
        var job = await rig.JobAsync(dto.Id);
        Assert.Equal("text/csv; charset=utf-8", download.Value.ContentType);
        Assert.Equal($"scripted-{job.CreatedAtUtc:yyyyMMdd-HHmmss}.csv", download.Value.DownloadName);
        Assert.Equal((job.Sha256, job.FileSizeBytes), (download.Value.Sha256, download.Value.SizeBytes));
        Assert.Equal(job.Sha256, Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
        Assert.Contains(AuditActions.ExportDownloaded, await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task ADownloadWhoseAuditRowCannotBeWritten_ReturnsNoFile()
    {
        await using var rig = await ExportRig.CreateAsync(throwingAudit: true);
        var dto = await rig.RequestAsync();
        await rig.DrainAsync();

        var download = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);

        Assert.Equal(("ChronoAudit.AuditUnavailable", ChronoAuditErrorKind.Unavailable), (download.Error.Code, download.Error.Kind));
    }

    [Fact]
    public async Task WithoutAnAuditedModule_ExportsStillWork_AndTheAuditStepIsSkippedWithOneWarning()
    {
        await using var rig = await ExportRig.CreateAsync(withAuditedModule: false);
        var dto = await rig.RequestAsync();
        await rig.DrainAsync();

        var first = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);
        var second = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);
        await first.Value.Content.DisposeAsync();
        await second.Value.Content.DisposeAsync();

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(ExportState.Completed, (await rig.JobAsync(dto.Id)).State);
        Assert.Single(rig.Host.Logs.Entries, e => e.Message.Contains("No audited module", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADoubleDownload_IsTwoAuditedEvents_WithTheSameContent()
    {
        await using var rig = await ExportRig.CreateAsync();
        var dto = await rig.RequestAsync();
        await rig.DrainAsync();

        var contents = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            var download = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);
            await using var stream = download.Value.Content;
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            contents.Add(copy.ToArray());
        }

        Assert.Equal(contents[0], contents[1]);
        Assert.Equal(2, (await rig.AuditActionsAsync()).Count(a => a == AuditActions.ExportDownloaded));
    }

    [Fact]
    public async Task ACancelledRequest_StopsWithCancellation_NotWithAResultOrAnAuditRow()
    {
        await using var rig = await ExportRig.CreateAsync();
        var dto = await rig.RequestAsync();
        await rig.DrainAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.DownloadAsync(dto.Id, ExportRig.Me, cts.Token));

        Assert.DoesNotContain(AuditActions.ExportDownloaded, await rig.AuditActionsAsync());
    }

    [Fact]
    public async Task ACancelOfAPendingJob_WritesItsAuditRow_AndAFinishedJobCannotBeCancelled()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.MaxActivePerUser = 5);
        var pending = await rig.RequestAsync();
        var done = await rig.RequestAsync();

        var cancelled = await rig.Service.CancelAsync(pending.Id, ExportRig.Me, CancellationToken.None);
        await rig.DrainAsync();
        var tooLate = await rig.Service.CancelAsync(done.Id, ExportRig.Me, CancellationToken.None);

        Assert.Equal(ExportState.Cancelled, cancelled.Value.State);
        Assert.Equal(("Export.AlreadyFinished", ChronoAuditErrorKind.Conflict), (tooLate.Error.Code, tooLate.Error.Kind));
        Assert.Single(await rig.AuditActionsAsync(), AuditActions.ExportCancelled);
    }

    [Fact]
    public async Task TheRequestIsRefused_ForAnUnknownProducer_ABadFormat_OversizedParameters_AndAnAnonymousCaller()
    {
        await using var rig = await ExportRig.CreateAsync();
        var json = ExportRig.Json("{}");

        var unknown = await rig.Service.RequestAsync(new ExportRequest("nope", ExportFormat.Csv, json), ExportRig.Me, CancellationToken.None);
        var format = await rig.Service.RequestAsync(new ExportRequest("scripted", (ExportFormat)9, json), ExportRig.Me, CancellationToken.None);
        var big = await rig.Service.RequestAsync(new ExportRequest("scripted", ExportFormat.Csv, ExportRig.Json("{\"x\":\"" + new string('a', 17_000) + "\"}")), ExportRig.Me, CancellationToken.None);
        var anonymous = await rig.Service.RequestAsync(new ExportRequest("scripted", ExportFormat.Csv, json), ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.Equal(("Export.UnknownProducer", ChronoAuditErrorKind.Validation), (unknown.Error.Code, unknown.Error.Kind));
        Assert.Equal("ChronoAudit.Validation", format.Error.Code);
        Assert.Equal("ChronoAudit.Validation", big.Error.Code);
        Assert.Equal("ChronoAudit.Validation", anonymous.Error.Code);
        using var scope = rig.Host.Provider.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StoreDb>().Set<ExportJob>().ToListAsync());
    }

    [Fact]
    public async Task AListIsBounded_AndNewestFirst()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.MaxActivePerUser = 50);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            rig.Host.Clock.Advance(TimeSpan.FromSeconds(1));
            ids.Add((await rig.RequestAsync()).Id);
        }

        var two = (await rig.Service.ListAsync(ExportRig.Me, 2, CancellationToken.None)).Value;
        var many = (await rig.Service.ListAsync(ExportRig.Me, 10_000, CancellationToken.None)).Value;
        var zero = (await rig.Service.ListAsync(ExportRig.Me, 0, CancellationToken.None)).Value;

        Assert.Equal(new[] { ids[3], ids[2] }, two.Select(j => j.Id));
        Assert.Equal(4, many.Count);
        Assert.Single(zero);
    }

    [Fact]
    public async Task TheFileStore_NeverLeavesItsFolder()
    {
        await using var rig = await ExportRig.CreateAsync();
        var store = rig.Host.Provider.GetRequiredService<ExportFileStore>();
        var root = Path.GetFullPath(rig.Folder);

        Assert.Equal(Path.Combine(root, "a.csv"), store.PathOf("a.csv"));
        Assert.Null(store.PathOf(Path.Combine("..", "outside.csv")));
        Assert.Null(store.PathOf(Path.Combine("sub", "..", "..", "outside.csv")));
        Assert.Null(store.PathOf(Path.GetFullPath(Path.Combine(root, "..", "x.csv"))));
        Assert.Null(store.PathOf(string.Empty));
        Assert.Null(store.OpenRead(Path.Combine("..", "outside.csv")));
        Assert.Null(store.OpenRead("missing.csv"));
    }

    [Fact]
    public async Task ARelativeDirectory_IsResolvedAgainstTheApplicationBase_NotTheCurrentDirectory()
    {
        var relative = Path.Combine("App_Data", "export-test-" + Guid.NewGuid().ToString("N"));
        await using var rig = await ExportRig.CreateAsync(o => o.Directory = relative);
        var dto = await rig.RequestAsync();

        await rig.DrainAsync();

        var expectedRoot = Path.Combine(AppContext.BaseDirectory, relative);
        try
        {
            Assert.True(File.Exists(Path.Combine(expectedRoot, (await rig.JobAsync(dto.Id)).FileName!)));
        }
        finally
        {
            if (Directory.Exists(expectedRoot))
            {
                Directory.Delete(expectedRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("RetentionDays", 0)]
    [InlineData("LeaseSeconds", 10)]
    [InlineData("PollIntervalSeconds", 0)]
    [InlineData("MaxActivePerUser", 0)]
    [InlineData("BatchSize", 50)]
    public void TheOptions_RefuseValuesOutOfRange(string name, int value)
    {
        var options = new ExportOptions();
        typeof(ExportOptions).GetProperty(name)!.SetValue(options, value);

        Assert.Throws<InvalidOperationException>(options.Validate);
        new ExportOptions().Validate();
    }
}

public sealed class ExportRetentionTests
{
    [Fact]
    public async Task OldFiles_AreDeleted_AndTheDownloadSaysGone()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.RetentionDays = 7);
        var old = await rig.RequestAsync();
        await rig.DrainAsync();
        var oldJob = await rig.JobAsync(old.Id);
        rig.Host.Clock.Advance(TimeSpan.FromDays(3));
        var recent = await rig.RequestAsync();
        await rig.DrainAsync();
        var recentJob = await rig.JobAsync(recent.Id);
        rig.Host.Clock.Advance(TimeSpan.FromDays(5));

        var ran = await rig.Worker.RunOnceAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.False(File.Exists(rig.PathOf(oldJob)));
        Assert.True(File.Exists(rig.PathOf(recentJob)), "only files older than the retention are removed");
        Assert.Equal(ExportState.Completed, (await rig.JobAsync(old.Id)).State);
        var gone = await rig.Service.DownloadAsync(old.Id, ExportRig.Me, CancellationToken.None);
        Assert.Equal(("Export.FileMissing", ChronoAuditErrorKind.NotFound), (gone.Error.Code, gone.Error.Kind));
    }

    [Fact]
    public async Task TheCleanUp_RunsAtMostOncePerHour_NotOnEveryIdleLook()
    {
        await using var rig = await ExportRig.CreateAsync(o => o.RetentionDays = 7);
        var dto = await rig.RequestAsync();
        await rig.DrainAsync();
        var job = await rig.JobAsync(dto.Id);

        // The first idle look sets the hour mark and finds nothing old.
        rig.Host.Clock.Advance(TimeSpan.FromHours(1));
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        await rig.SetFinishedAsync(dto.Id, rig.Host.Clock.GetUtcNow().AddDays(-30));

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(30));
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(rig.PathOf(job)), "half an hour later the clean-up has not run again");

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(40));
        await rig.Worker.RunOnceAsync(CancellationToken.None);
        Assert.False(File.Exists(rig.PathOf(job)));
    }
}

public sealed class ExportProducerTests
{
    private static readonly DateTimeOffset Wide = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Period() => $"\"from\":\"{Wide.AddDays(-30):O}\",\"to\":\"{Wide.AddDays(30):O}\"";

    private static async Task<string> RunToTextAsync(ExportRig rig, string producer, string parameters, ChronoAuditScope scope, ExportFormat format = ExportFormat.Csv)
    {
        var dto = await rig.RequestAsync(producer, format, parameters, scope);
        await rig.DrainAsync();
        var job = await rig.JobAsync(dto.Id);
        Assert.True(job.State == ExportState.Completed, job.ErrorCode + ": " + job.ErrorMessage);
        var bytes = await File.ReadAllBytesAsync(rig.PathOf(job));
        return format == ExportFormat.Csv ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : string.Empty;
    }

    private static async Task AddOrdersAsync(ExportRig rig, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var scope = rig.Host.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.Orders.Add(new Order { Title = "order-" + i });
            await db.SaveChangesAsync();
            rig.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task AHostProducer_ByKey_Works_AndAnUnknownKeyIsRefused()
    {
        await using var rig = await ExportRig.CreateAsync();

        var text = await RunToTextAsync(rig, "scripted", "{}", ExportRig.Me);
        var unknown = await rig.Service.RequestAsync(new ExportRequest("nope", ExportFormat.Csv, ExportRig.Json("{}")), ExportRig.Me, CancellationToken.None);

        Assert.StartsWith("n\r\nrow-0-0\r\n", text, StringComparison.Ordinal);
        Assert.Equal("Export.UnknownProducer", unknown.Error.Code);
    }

    [SqliteDecimalFact]
    public async Task TheAnalyticsProducer_WritesTheResultAsOneTable_WithTheCatalogTitles()
    {
        await using var rig = await ExportRig.CreateAsync();
        await rig.Host.LogAsync(1, method: "GET");
        await rig.Host.LogAsync(2, method: "POST");
        await rig.Host.LogAsync(3, method: "POST");
        var parameters = "{\"definition\":{\"subject\":\"activity\",\"dimensions\":[\"method\"],\"measures\":[\"count\"]," + Period() + ",\"sortBy\":\"count\"}}";

        var text = await RunToTextAsync(rig, "analytics", parameters, ExportRig.Me);

        Assert.Equal("Method,Calls\r\nPOST,2\r\nGET,1\r\n", text);
    }

    [SqliteDecimalFact]
    public async Task TheAnalyticsProducer_RunsASavedReport_WithANewPeriod_AndASavedReportTheCallerCannotSeeIs404AtRequest()
    {
        await using var rig = await ExportRig.CreateAsync();
        await rig.Host.LogAsync(1, method: "GET");
        var saved = await rig.Host.Provider.GetRequiredService<ISavedReportService>().CreateAsync(
            new SavedReportInput("calls", new AnalyticsQuery("activity", ["method"], ["count"], null, Wide.AddDays(-400), Wide.AddDays(-399))), ExportRig.Me, CancellationToken.None);

        var empty = await RunToTextAsync(rig, "analytics", $$"""{"savedReportId":"{{saved.Value.Id}}"}""", ExportRig.Me);
        var moved = await RunToTextAsync(rig, "analytics", $$"""{"savedReportId":"{{saved.Value.Id}}",{{Period()}}}""", ExportRig.Me);
        var stranger = await rig.Service.RequestAsync(
            new ExportRequest("analytics", ExportFormat.Csv, ExportRig.Json($$"""{"savedReportId":"{{saved.Value.Id}}"}""")), ChronoAuditScope.OwnOf("u2"), CancellationToken.None);

        Assert.Equal("Method,Calls\r\n", empty);
        Assert.Equal("Method,Calls\r\nGET,1\r\n", moved);
        Assert.Equal(("SavedReport.NotFound", ChronoAuditErrorKind.NotFound), (stranger.Error.Code, stranger.Error.Kind));
    }

    [Fact]
    public async Task TheAnalyticsProducer_RefusesADefinitionTheCatalogRefuses_AtRequest()
    {
        await using var rig = await ExportRig.CreateAsync();
        var parameters = "{\"definition\":{\"subject\":\"activity\",\"dimensions\":[\"nope\"],\"measures\":[\"count\"]," + Period() + "}}";

        var result = await rig.Service.RequestAsync(new ExportRequest("analytics", ExportFormat.Csv, ExportRig.Json(parameters)), ExportRig.Me, CancellationToken.None);
        var empty = await rig.Service.RequestAsync(new ExportRequest("analytics", ExportFormat.Csv, ExportRig.Json("{}")), ExportRig.Me, CancellationToken.None);

        Assert.Equal("Filter.NotAllowed", result.Error.Code);
        Assert.Equal("ChronoAudit.Validation", empty.Error.Code);
    }

    [Fact]
    public async Task TheAuditProducer_WritesTheRowsOfAModuleNewestFirst_AndNeedsTheAllScope()
    {
        await using var rig = await ExportRig.CreateAsync();
        await AddOrdersAsync(rig, 3);
        var parameters = $$"""{"module":"shop",{{Period()}}}""";

        var text = await RunToTextAsync(rig, "audit", parameters, ExportRig.Admin);
        var denied = await rig.Service.RequestAsync(new ExportRequest("audit", ExportFormat.Csv, ExportRig.Json(parameters)), ExportRig.Me, CancellationToken.None);
        var unknownModule = await rig.Service.RequestAsync(
            new ExportRequest("audit", ExportFormat.Csv, ExportRig.Json($$"""{"module":"nope",{{Period()}}}""")), ExportRig.Admin, CancellationToken.None);
        var tooWide = await rig.Service.RequestAsync(
            new ExportRequest("audit", ExportFormat.Csv, ExportRig.Json($$"""{"module":"shop","from":"2025-01-01T00:00:00Z","to":"2026-12-01T00:00:00Z"}""")), ExportRig.Admin, CancellationToken.None);

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Sequence,OccurredAtUtc,ActorUserId,Action,", lines[0], StringComparison.Ordinal);
        Assert.Equal(4, lines.Length - 1);
        Assert.Single(lines.Skip(1), l => l.Contains(AuditActions.ExportStarted, StringComparison.Ordinal));
        var sequences = lines.Skip(1).Select(l => long.Parse(l.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(sequences.OrderByDescending(s => s), sequences);
        Assert.Equal(("Export.KindForbidden", ChronoAuditErrorKind.Forbidden), (denied.Error.Code, denied.Error.Kind));
        Assert.Equal("Table.NotFound", unknownModule.Error.Code);
        Assert.Equal("ChronoAudit.Validation", tooWide.Error.Code);
    }

    [Fact]
    public async Task TheExtractProducer_WritesTheMaskedHistory_AndNeedsTheAllScope()
    {
        await using var rig = await ExportRig.CreateAsync();
        await AddOrdersAsync(rig, 2);
        var parameters = "{" + Period() + "}";

        var text = await RunToTextAsync(rig, "extract", parameters, ExportRig.Admin);
        var denied = await rig.Service.RequestAsync(new ExportRequest("extract", ExportFormat.Csv, ExportRig.Json(parameters)), ExportRig.Me, CancellationToken.None);

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Module,Sequence,OccurredAtUtc,ActorUserId,Action,", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length - 1);
        Assert.Single(lines.Skip(1), l => l.Contains(AuditActions.ExportStarted, StringComparison.Ordinal));
        Assert.All(lines.Skip(1), l => Assert.StartsWith("shop,", l, StringComparison.Ordinal));
        Assert.Equal("Export.KindForbidden", denied.Error.Code);
    }

    [Fact]
    public async Task TheActivityProducer_PinsAnOwnScopeToItsOwnCalls_AndRefusesAskingForAnotherUser()
    {
        await using var rig = await ExportRig.CreateAsync();
        await rig.Host.LogAsync(1, userId: "u1");
        await rig.Host.LogAsync(2, userId: "u2");
        await rig.Host.LogAsync(3, userId: "u1");
        var parameters = "{" + Period() + "}";

        var mine = await RunToTextAsync(rig, "activity", parameters, ExportRig.Me);
        var all = await RunToTextAsync(rig, "activity", $$"""{"from":"{{Wide.AddDays(-1):O}}","to":"{{Wide.AddDays(5):O}}"}""", ExportRig.Admin);
        var other = await rig.Service.RequestAsync(
            new ExportRequest("activity", ExportFormat.Csv, ExportRig.Json($$"""{"userId":"u2",{{Period()}}}""")), ExportRig.Me, CancellationToken.None);

        Assert.Equal(2, mine.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.DoesNotContain("/api/items/2,", mine, StringComparison.Ordinal);
        Assert.Equal(3, all.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Equal(("Export.KindForbidden", ChronoAuditErrorKind.Forbidden), (other.Error.Code, other.Error.Kind));
    }

    [Theory]
    [InlineData(31, false, true)]
    [InlineData(32, false, false)]
    [InlineData(366, true, true)]
    [InlineData(367, true, false)]
    public async Task TheActivityExportRange_FollowsTheLogRule(int days, bool narrowedByUser, bool valid)
    {
        await using var rig = await ExportRig.CreateAsync();
        var user = narrowedByUser ? ",\"userId\":\"admin\"" : string.Empty;
        var parameters = $"{{\"from\":\"{Wide:O}\",\"to\":\"{Wide.AddDays(days):O}\"{user}}}";

        var result = await rig.Service.RequestAsync(new ExportRequest("activity", ExportFormat.Csv, ExportRig.Json(parameters)), ExportRig.Admin, CancellationToken.None);

        Assert.Equal(valid, result.IsSuccess);
        if (!valid)
        {
            Assert.Equal("ChronoAudit.Validation", result.Error.Code);
        }
    }

    [Fact]
    public async Task AnXlsxExport_Completes_TheFileIsAZipWithTheSheet_AndTheJobShaIsTheShaOfTheFile()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.Batches = 3;
        rig.Script.RowsPerBatch = 4;
        await rig.Host.LogAsync(1);
        var dto = await rig.RequestAsync("scripted", ExportFormat.Xlsx);

        await rig.DrainAsync();

        var job = await rig.JobAsync(dto.Id);
        Assert.Equal(ExportState.Completed, job.State);
        var path = rig.PathOf(job);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.EndsWith(".xlsx", path, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), job.Sha256);
        Assert.Equal(bytes.Length, job.FileSizeBytes);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".spool")), "the spool is removed once the workbook is complete");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Assert.Contains("row-2-3", sheet, StringComparison.Ordinal);
        Assert.Equal(13, sheet.Split("<row ").Length - 1);
        var download = await rig.Service.DownloadAsync(dto.Id, ExportRig.Me, CancellationToken.None);
        await download.Value.Content.DisposeAsync();
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", download.Value.ContentType);
        Assert.EndsWith(".xlsx", download.Value.DownloadName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnXlsxExport_CrashedAfterASpoolBatch_ResumesWithoutDuplicates()
    {
        await using var rig = await ExportRig.CreateAsync();
        rig.Script.Batches = 3;
        rig.Script.FailOnCall = 3;
        var dto = await rig.RequestAsync("scripted", ExportFormat.Xlsx);

        await rig.DrainAsync();

        var job = await rig.JobAsync(dto.Id);
        using var zip = new ZipArchive(new MemoryStream(await File.ReadAllBytesAsync(rig.PathOf(job))), ZipArchiveMode.Read);
        var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Assert.Equal(ExportState.Completed, job.State);
        Assert.Equal(7, sheet.Split("<row ").Length - 1);
        Assert.Equal(1, sheet.Split("row-1-1").Length - 1);
    }
}
