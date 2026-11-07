using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class SavedReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static ChronoAuditScope Me(string user = "u1") => ChronoAuditScope.OwnOf(user);

    private static ChronoAuditScope InGroup(string user, string group) => ChronoAuditScope.GroupOf(user, group);

    private static ChronoAuditScope Everyone(string user = "admin") => ChronoAuditScope.Everything(user);

    private static AnalyticsQuery Definition(string subject = "activity", string dimension = "method")
        => new(subject, [dimension], ["count"], null, T0, T0.AddDays(30));

    private static SavedReportInput Report(string title = "Monthly calls", string? group = null, string subject = "activity", string dimension = "method")
        => new(title, Definition(subject, dimension), group);

    private static async Task<ActivityHost> HostAsync()
    {
        var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics().AddSavedReports());
        host.Clock.Set(T0);
        return host;
    }

    private static ISavedReportService Service(ActivityHost host) => host.Provider.GetRequiredService<ISavedReportService>();

    private static async Task<SavedReportDto> SaveAsync(ActivityHost host, ChronoAuditScope who, string title = "Monthly calls", string? group = null)
        => (await Service(host).CreateAsync(Report(title, group), who, CancellationToken.None)).Value;

    private static async Task<int> CountAsync(ActivityHost host)
    {
        using var scope = host.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StoreDb>().Set<SavedReport>().CountAsync();
    }

    // ---- create, read, update, delete ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACreatedReport_IsStoredWithItsDefinition_AndReadBackByItsOwner()
    {
        await using var host = await HostAsync();

        var created = await Service(host).CreateAsync(Report("  Monthly sales  "), Me(), CancellationToken.None);
        var read = await Service(host).GetAsync(created.Value.Id, Me(), CancellationToken.None);

        Assert.True(created.IsSuccess);
        Assert.Equal("Monthly sales", read.Value.Title);
        Assert.True(read.Value.IsMine);
        Assert.False(read.Value.IsShared);
        Assert.Equal("activity", read.Value.Subject);
        Assert.Equal(new[] { "method" }, read.Value.Definition!.Dimensions);
        Assert.Equal(new[] { "count" }, read.Value.Definition.Measures);
        Assert.Equal((T0, T0.AddDays(30)), (read.Value.Definition.From, read.Value.Definition.To));
        Assert.Equal(1, await CountAsync(host));
    }

    [Fact]
    public async Task AFilterTree_SurvivesTheStoredJson()
    {
        await using var host = await HostAsync();
        var filters = new[]
        {
            FilterNode.Condition("method", FilterOp.In, "GET", "POST"),
            FilterNode.AnyOf(FilterNode.Condition("endpoint", FilterOp.Prefix, "/api/"), FilterNode.AllOf(FilterNode.Condition("status", FilterOp.Range, "500", string.Empty))),
        };
        var input = new SavedReportInput("filtered", Definition() with { Filters = filters }, null);

        var created = await Service(host).CreateAsync(input, Me(), CancellationToken.None);
        var read = (await Service(host).GetAsync(created.Value.Id, Me(), CancellationToken.None)).Value.Definition!;

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        Assert.Equal(2, read.Filters!.Count);
        Assert.Equal(FilterOp.In, read.Filters[0].Op);
        Assert.Equal(new[] { "GET", "POST" }, read.Filters[0].Values);
        Assert.Equal(FilterOp.Prefix, read.Filters[1].Or![0].Op);
        Assert.Equal("500", read.Filters[1].Or![1].And![0].Values![0]);
    }

    [Fact]
    public async Task ADefinitionTheCatalogRefuses_IsNotStored_AndTheRunnersErrorIsReturned()
    {
        await using var host = await HostAsync();

        var unknownKey = await Service(host).CreateAsync(Report(dimension: "nope"), Me(), CancellationToken.None);
        var unknownSubject = await Service(host).CreateAsync(Report(subject: "zzz"), Me(), CancellationToken.None);
        var forbiddenSubject = await Service(host).CreateAsync(Report(subject: "audit:shop", dimension: "action"), Me(), CancellationToken.None);

        Assert.Equal("Filter.NotAllowed", unknownKey.Error.Code);
        Assert.Equal("Analytics.SubjectNotFound", unknownSubject.Error.Code);
        Assert.Equal("Analytics.SubjectForbidden", forbiddenSubject.Error.Code);
        Assert.Equal(0, await CountAsync(host));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankTitle_IsRefused(string title)
    {
        await using var host = await HostAsync();

        var result = await Service(host).CreateAsync(Report(title), Me(), CancellationToken.None);

        Assert.Equal("SavedReport.TitleInvalid", result.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Validation, result.Error.Kind);
        Assert.Equal("SavedReport.TitleInvalid", SavedReport.Create("u1", title, "activity", "{}", null, T0).Error.Code);
        Assert.Equal(0, await CountAsync(host));
    }

    [Fact]
    public async Task ATooLongTitleOrDefinition_IsRefused()
    {
        Assert.Equal("SavedReport.TitleInvalid", SavedReport.Create("u1", new string('a', SavedReport.MaxTitleLength + 1), "activity", "{}", null, T0).Error.Code);
        Assert.True(SavedReport.Create("u1", new string('a', SavedReport.MaxTitleLength), "activity", "{}", null, T0).IsSuccess);
        Assert.Equal("SavedReport.DefinitionInvalid", SavedReport.Create("u1", "t", "activity", new string('x', SavedReport.MaxDefinitionLength + 1), null, T0).Error.Code);
        Assert.Equal("SavedReport.DefinitionInvalid", SavedReport.Create("u1", "t", "activity", " ", null, T0).Error.Code);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnAnonymousCaller_CannotSaveAReport()
    {
        await using var host = await HostAsync();

        var result = await Service(host).CreateAsync(Report(), ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.Equal("ChronoAudit.Validation", result.Error.Code);
        Assert.Equal(0, await CountAsync(host));
    }

    [Fact]
    public async Task OnlyTheOwnerChangesOrDeletes_AStrangerGets404_AndASharedViewerGets403()
    {
        await using var host = await HostAsync();
        var owner = InGroup("owner", "gA");
        var saved = await SaveAsync(host, owner, "mine", group: "gA");
        var colleague = InGroup("colleague", "gA");
        var stranger = InGroup("stranger", "gB");
        var body = Report("changed", "gA");

        var colleagueUpdate = await Service(host).UpdateAsync(saved.Id, body, null, colleague, CancellationToken.None);
        var colleagueDelete = await Service(host).DeleteAsync(saved.Id, null, colleague, CancellationToken.None);
        var strangerUpdate = await Service(host).UpdateAsync(saved.Id, body, null, stranger, CancellationToken.None);
        var strangerDelete = await Service(host).DeleteAsync(saved.Id, null, stranger, CancellationToken.None);

        Assert.Equal(("SavedReport.NotOwner", ChronoAuditErrorKind.Forbidden), (colleagueUpdate.Error.Code, colleagueUpdate.Error.Kind));
        Assert.Equal("SavedReport.NotOwner", colleagueDelete.Error.Code);
        Assert.Equal(("SavedReport.NotFound", ChronoAuditErrorKind.NotFound), (strangerUpdate.Error.Code, strangerUpdate.Error.Kind));
        Assert.Equal("SavedReport.NotFound", strangerDelete.Error.Code);
        Assert.Equal("mine", (await Service(host).GetAsync(saved.Id, owner, CancellationToken.None)).Value.Title);

        Assert.Equal("changed", (await Service(host).UpdateAsync(saved.Id, body, null, owner, CancellationToken.None)).Value.Title);
        Assert.True((await Service(host).DeleteAsync(saved.Id, null, owner, CancellationToken.None)).IsSuccess);
        Assert.Equal(0, await CountAsync(host));
    }

    [Fact]
    public async Task AnUpdate_ChangesTheStampAndTheTime_AndKeepsTheOwner()
    {
        await using var host = await HostAsync();
        var saved = await SaveAsync(host, Me());
        host.Clock.Set(T0.AddHours(5));

        var updated = (await Service(host).UpdateAsync(saved.Id, Report("renamed"), saved.ConcurrencyStamp, Me(), CancellationToken.None)).Value;

        Assert.NotEqual(saved.ConcurrencyStamp, updated.ConcurrencyStamp);
        Assert.Equal(saved.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal(T0.AddHours(5), updated.UpdatedAtUtc);
        Assert.True(updated.IsMine);
    }

    // ---- who sees what -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APrivateReport_IsSeenByNobodyElse_NotEvenTheAllScope_AndASharedOneByItsGroupAndTheAllScope()
    {
        await using var host = await HostAsync();
        var owner = InGroup("owner", "gA");
        var privateOne = await SaveAsync(host, owner, "private");
        var sharedOne = await SaveAsync(host, owner, "shared", group: "gA");
        var sameGroup = InGroup("colleague", "gA");
        var otherGroup = InGroup("stranger", "gB");
        var everything = Everyone();
        var service = Service(host);

        Assert.Equal(new[] { "shared" }, (await service.ListAsync(sameGroup, 50, CancellationToken.None)).Value.Select(r => r.Title));
        Assert.Equal(new[] { "shared" }, (await service.ListAsync(everything, 50, CancellationToken.None)).Value.Select(r => r.Title));
        Assert.Empty((await service.ListAsync(otherGroup, 50, CancellationToken.None)).Value);
        Assert.Equal(new[] { "private", "shared" }, (await service.ListAsync(owner, 50, CancellationToken.None)).Value.Select(r => r.Title).Order());

        Assert.Equal("SavedReport.NotFound", (await service.GetAsync(privateOne.Id, everything, CancellationToken.None)).Error.Code);
        Assert.Equal("SavedReport.NotFound", (await service.GetAsync(sharedOne.Id, otherGroup, CancellationToken.None)).Error.Code);
        Assert.False((await service.GetAsync(sharedOne.Id, sameGroup, CancellationToken.None)).Value.IsMine);
        Assert.False((await service.GetAsync(sharedOne.Id, everything, CancellationToken.None)).Value.IsMine);
    }

    [Fact]
    public async Task AnOwnScopeUser_NeverSeesAGroupsReport()
    {
        await using var host = await HostAsync();
        await SaveAsync(host, InGroup("owner", "gA"), "shared", group: "gA");

        Assert.Empty((await Service(host).ListAsync(Me("colleague"), 50, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task AListIsBounded_NewestChangeFirst_AndLeavesTheDefinitionOut()
    {
        await using var host = await HostAsync();
        for (var i = 0; i < 5; i++)
        {
            host.Clock.Set(T0.AddMinutes(i));
            await SaveAsync(host, Me(), "r" + i);
        }

        var three = (await Service(host).ListAsync(Me(), 3, CancellationToken.None)).Value;
        var huge = (await Service(host).ListAsync(Me(), 100_000, CancellationToken.None)).Value;
        var zero = (await Service(host).ListAsync(Me(), 0, CancellationToken.None)).Value;

        Assert.Equal(new[] { "r4", "r3", "r2" }, three.Select(r => r.Title));
        Assert.Equal(5, huge.Count);
        Assert.Single(zero);
        Assert.All(three, r => Assert.Null(r.Definition));
    }

    [Fact]
    public async Task SharingIsOnlyIntoOnesOwnGroup_ExceptWithTheAllScope()
    {
        await using var host = await HostAsync();
        var service = Service(host);

        var otherGroup = await service.CreateAsync(Report(group: "gB"), InGroup("u1", "gA"), CancellationToken.None);
        var fromOwn = await service.CreateAsync(Report(group: "gA"), Me(), CancellationToken.None);
        var ownGroup = await service.CreateAsync(Report(group: "gA"), InGroup("u1", "gA"), CancellationToken.None);
        var anyGroup = await service.CreateAsync(Report(group: "gB"), Everyone(), CancellationToken.None);
        var blank = await service.CreateAsync(Report(group: "  "), Everyone(), CancellationToken.None);

        Assert.Equal(("SavedReport.ShareNotAllowed", ChronoAuditErrorKind.Forbidden), (otherGroup.Error.Code, otherGroup.Error.Kind));
        Assert.Equal("SavedReport.ShareNotAllowed", fromOwn.Error.Code);
        Assert.True(ownGroup.IsSuccess);
        Assert.True(anyGroup.IsSuccess);
        Assert.True(anyGroup.Value.IsShared);
        Assert.Equal("ChronoAudit.Validation", blank.Error.Code);
        Assert.Equal(2, await CountAsync(host));
    }

    [Fact]
    public async Task AnUpdate_CannotShareIntoAnotherGroup_AndNothingChanges()
    {
        await using var host = await HostAsync();
        var owner = InGroup("owner", "gA");
        var saved = await SaveAsync(host, owner, "keep");

        var result = await Service(host).UpdateAsync(saved.Id, Report("changed", "gB"), null, owner, CancellationToken.None);

        Assert.Equal("SavedReport.ShareNotAllowed", result.Error.Code);
        var read = (await Service(host).GetAsync(saved.Id, owner, CancellationToken.None)).Value;
        Assert.Equal("keep", read.Title);
        Assert.False(read.IsShared);
    }

    // ---- run -----------------------------------------------------------------------------------------------------------------------

    [SqliteDecimalFact]
    public async Task Running_UsesTheSavedDefinition_AndAnOptionalPeriodOverride()
    {
        await using var host = await HostAsync();
        var saved = await SaveAsync(host, Me());

        var saved1 = await Service(host).RunAsync(saved.Id, null, null, Me(), CancellationToken.None);
        var overridden = await Service(host).RunAsync(saved.Id, T0.AddDays(100), T0.AddDays(120), Me(), CancellationToken.None);

        Assert.True(saved1.IsSuccess, saved1.IsFailure ? saved1.Error.Message : null);
        Assert.Equal((T0, T0.AddDays(30)), (saved1.Value.From, saved1.Value.To));
        Assert.Equal((T0.AddDays(100), T0.AddDays(120)), (overridden.Value.From, overridden.Value.To));
    }

    [SqliteDecimalFact]
    public async Task ARunOfAnInvisibleReport_Is404_AndNothingIsRun()
    {
        await using var host = await HostAsync();
        var saved = await SaveAsync(host, Me("owner"));
        await host.LogAsync(1, userId: "stranger");

        var result = await Service(host).RunAsync(saved.Id, null, null, Me("stranger"), CancellationToken.None);

        Assert.Equal(("SavedReport.NotFound", ChronoAuditErrorKind.NotFound), (result.Error.Code, result.Error.Kind));
    }

    [Fact]
    public async Task AKeyTheCatalogNoLongerHas_MakesTheRunOutdated_422_NotADifferentReport()
    {
        await using var host = await HostAsync();
        var stale = SavedReport.Create("u1", "old", "activity", """{"subject":"activity","dimensions":["gone"],"measures":["count"],"from":"2026-11-01T00:00:00+00:00","to":"2026-11-30T00:00:00+00:00"}""", null, T0).Value;
        await AddRowAsync(host, stale);

        var result = await Service(host).RunAsync(stale.Id, null, null, Me(), CancellationToken.None);

        Assert.Equal(("SavedReport.Outdated", ChronoAuditErrorKind.Unprocessable), (result.Error.Code, result.Error.Kind));
        Assert.Contains("gone", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherRunnerError_IsPassedThrough_NotCalledOutdated()
    {
        await using var host = await HostAsync();
        var adminReport = (await Service(host).CreateAsync(Report("audit", "gA", "audit:shop", "action"), Everyone(), CancellationToken.None)).Value;

        var result = await Service(host).RunAsync(adminReport.Id, null, null, InGroup("u2", "gA"), CancellationToken.None);

        Assert.Equal(("Analytics.SubjectForbidden", ChronoAuditErrorKind.Forbidden), (result.Error.Code, result.Error.Kind));
    }

    [Fact]
    public async Task AStoredTextThatIsNoLongerADefinition_IsOutdated_NotAnException()
    {
        await using var host = await HostAsync();
        var broken = SavedReport.Create("u1", "broken", "activity", "{not json", null, T0).Value;
        await AddRowAsync(host, broken);

        var result = await Service(host).RunAsync(broken.Id, null, null, Me(), CancellationToken.None);
        var read = await Service(host).GetAsync(broken.Id, Me(), CancellationToken.None);

        Assert.Equal("SavedReport.Outdated", result.Error.Code);
        Assert.True(read.IsSuccess);
        Assert.Null(read.Value.Definition);
    }

    // ---- concurrency (G09) ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConcurrentChange_IsAConflict()
    {
        await using var host = await HostAsync();
        var saved = await SaveAsync(host, Me());
        var service = Service(host);

        var first = await service.UpdateAsync(saved.Id, Report("first"), saved.ConcurrencyStamp, Me(), CancellationToken.None);
        var second = await service.UpdateAsync(saved.Id, Report("second"), saved.ConcurrencyStamp, Me(), CancellationToken.None);
        var deleteWithOldStamp = await service.DeleteAsync(saved.Id, saved.ConcurrencyStamp, Me(), CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(("SavedReport.Conflict", ChronoAuditErrorKind.Conflict), (second.Error.Code, second.Error.Kind));
        Assert.Equal("SavedReport.Conflict", deleteWithOldStamp.Error.Code);
        Assert.Equal("first", (await service.GetAsync(saved.Id, Me(), CancellationToken.None)).Value.Title);
    }

    [Fact]
    public async Task TwoContextsSavingTheSameReport_TheSecondSaveIsRefusedByTheDatabase()
    {
        await using var host = await HostAsync();
        var saved = await SaveAsync(host, Me());
        using var scopeA = host.Provider.CreateScope();
        using var scopeB = host.Provider.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<StoreDb>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<StoreDb>();
        var a = await dbA.Set<SavedReport>().SingleAsync(r => r.Id == saved.Id);
        var b = await dbB.Set<SavedReport>().SingleAsync(r => r.Id == saved.Id);

        Assert.True(a.Change("A", "activity", "{}", null, T0).IsSuccess);
        Assert.True(b.Change("B", "activity", "{}", null, T0).IsSuccess);
        await dbA.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        Assert.Equal("A", (await dbA.Set<SavedReport>().AsNoTracking().SingleAsync()).Title);
    }

    private static async Task AddRowAsync(ActivityHost host, SavedReport report)
    {
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
        db.Set<SavedReport>().Add(report);
        await db.SaveChangesAsync();
    }
}
