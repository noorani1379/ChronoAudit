using System.Net;
using System.Text.Json;
using ChronoAudit.AspNetCore;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class QueryGuardTests
{
    private static readonly string GuidString = Guid.NewGuid().ToString("D");

    public static readonly TheoryData<string> ReadRoutesWithUnknownKey = new()
    {
        "/chronoaudit/history/tables?typo=1",
        "/chronoaudit/history/shop/tables/Orders/changes?typo=1",
        $"/chronoaudit/history/shop/tables/Orders/records/{GuidString}/changes?typo=1",
        "/chronoaudit/audit/shop/search?typo=1",
        "/chronoaudit/activity/search?typo=1",
        "/chronoaudit/activity/export.csv?typo=1",
        "/chronoaudit/activity/statistics?typo=1",
        "/chronoaudit/activity/requests/1/body?typo=1",
        "/chronoaudit/activity/intents/incomplete?typo=1",
        "/chronoaudit/analytics/catalog?typo=1",
        "/chronoaudit/saved-reports?typo=1",
        $"/chronoaudit/saved-reports/{GuidString}?typo=1",
        "/chronoaudit/exports?typo=1",
        $"/chronoaudit/exports/{GuidString}?typo=1",
        $"/chronoaudit/exports/{GuidString}/download?typo=1",
        "/chronoaudit/extract/csv?typo=1",
        "/chronoaudit/timeline/subjects/order/1?typo=1",
        "/chronoaudit/timeline/groups/g1?typo=1",
        "/chronoaudit/timeline/actors/u1?typo=1",
        "/chronoaudit/timeline/subjects/order/1/evidence?typo=1",
    };

    [Theory]
    [MemberData(nameof(ReadRoutesWithUnknownKey))]
    public async Task AnUnknownQueryKey_IsRefused_OnEveryReadRoute(string route)
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.GetAsync(route, Who.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal("Filter.NotAllowed", body.RootElement.GetProperty("title").GetString());
        Assert.Contains("typo", body.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}

