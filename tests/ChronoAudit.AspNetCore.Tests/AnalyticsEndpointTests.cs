using System.Net;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class AnalyticsEndpointTests
{
    private static async Task SeedActivityAsync(HttpRig rig)
    {
        await rig.GetAsync("/api/hello", Who.Me("u1"));
        await rig.GetAsync("/api/hello", Who.Me("u1"));
        await rig.GetAsync("/api/teapot", Who.Me("u2"));
    }

    [Fact]
    public async Task TheCatalog_ListsTheSubjects_ScopeFiltered()
    {
        await using var rig = await HttpRig.StartAsync();

        var admin = await rig.GetAsync("/chronoaudit/analytics/catalog", Who.Admin);
        var own = await rig.GetAsync("/chronoaudit/analytics/catalog", Who.Me());

        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        using var adminBody = await HttpRig.JsonAsync(admin);
        using var ownBody = await HttpRig.JsonAsync(own);

        var adminKeys = adminBody.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        var ownKeys = ownBody.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();

        Assert.Contains("audit:shop", adminKeys);
        Assert.Contains("activity", adminKeys);
        Assert.Contains("timeline", adminKeys);

        Assert.DoesNotContain("audit:shop", ownKeys);
        Assert.Contains("activity", ownKeys);
        Assert.Contains("timeline", ownKeys);
    }

    [SqliteDecimalFact]
    public async Task ARun_RunsTheQuery_AndReturnsTheResult()
    {
        await using var rig = await HttpRig.StartAsync();
        await SeedActivityAsync(rig);

        var query = new
        {
            subject = "activity",
            dimensions = new[] { "method" },
            measures = new[] { "count" },
            from = HttpRig.T0.AddDays(-1).ToString("O"),
            to = HttpRig.T0.AddDays(1).ToString("O"),
        };

        var response = await rig.PostAsync("/chronoaudit/analytics/run", Who.Admin, json: query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal("method", body.RootElement.GetProperty("dimensions")[0].GetProperty("key").GetString());
        Assert.True(body.RootElement.GetProperty("rows").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ARun_WithoutSubjectPermission_Is403_AndABadKey_IsProblemWithTheStableCode()
    {
        await using var rig = await HttpRig.StartAsync();

        var forbiddenQuery = new
        {
            subject = "audit:shop",
            dimensions = new[] { "action" },
            measures = new[] { "count" },
            from = HttpRig.T0.AddDays(-1).ToString("O"),
            to = HttpRig.T0.AddDays(1).ToString("O"),
        };

        var forbidden = await rig.PostAsync("/chronoaudit/analytics/run", Who.Me(), json: forbiddenQuery);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var forbiddenBody = await HttpRig.JsonAsync(forbidden);
        Assert.Equal("Analytics.SubjectForbidden", forbiddenBody.RootElement.GetProperty("title").GetString());

        var unknownQuery = new
        {
            subject = "unknown_subject",
            dimensions = new[] { "action" },
            measures = new[] { "count" },
            from = HttpRig.T0.AddDays(-1).ToString("O"),
            to = HttpRig.T0.AddDays(1).ToString("O"),
        };

        var unknown = await rig.PostAsync("/chronoaudit/analytics/run", Who.Admin, json: unknownQuery);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unknownBody = await HttpRig.JsonAsync(unknown);
        Assert.Equal("Analytics.SubjectNotFound", unknownBody.RootElement.GetProperty("title").GetString());

        var badDimQuery = new
        {
            subject = "activity",
            dimensions = new[] { "bad_dimension" },
            measures = new[] { "count" },
            from = HttpRig.T0.AddDays(-1).ToString("O"),
            to = HttpRig.T0.AddDays(1).ToString("O"),
        };

        var badDim = await rig.PostAsync("/chronoaudit/analytics/run", Who.Admin, json: badDimQuery);
        Assert.Equal(HttpStatusCode.BadRequest, badDim.StatusCode);
        using var badDimBody = await HttpRig.JsonAsync(badDim);
        Assert.Equal("Filter.NotAllowed", badDimBody.RootElement.GetProperty("title").GetString());

        var tooWideQuery = new
        {
            subject = "activity",
            dimensions = new[] { "method" },
            measures = new[] { "count" },
            from = HttpRig.T0.ToString("O"),
            to = HttpRig.T0.AddMonths(40).ToString("O"),
        };

        var tooWide = await rig.PostAsync("/chronoaudit/analytics/run", Who.Admin, json: tooWideQuery);
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
        using var tooWideBody = await HttpRig.JsonAsync(tooWide);
        Assert.Equal("Analytics.RangeTooWide", tooWideBody.RootElement.GetProperty("title").GetString());
    }
}
