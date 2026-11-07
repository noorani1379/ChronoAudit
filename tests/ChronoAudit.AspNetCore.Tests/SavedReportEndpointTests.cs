using System.Net;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class SavedReportEndpointTests
{
    private static object SampleQuery(string subject = "activity") => new
    {
        subject,
        dimensions = new[] { "method" },
        measures = new[] { "count" },
        from = HttpRig.T0.AddDays(-1).ToString("O"),
        to = HttpRig.T0.AddDays(1).ToString("O"),
    };

    [Fact]
    public async Task Crud_WorksForOwner()
    {
        await using var rig = await HttpRig.StartAsync();

        var createPayload = new
        {
            title = "Activity Overview",
            definition = SampleQuery(),
        };

        var created = await rig.PostAsync("/chronoaudit/saved-reports", Who.Me("u1"), json: createPayload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();
        var stamp = createBody.RootElement.GetProperty("concurrencyStamp").GetGuid();
        Assert.Equal("Activity Overview", createBody.RootElement.GetProperty("title").GetString());

        var read = await rig.GetAsync($"/chronoaudit/saved-reports/{id}", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readBody = await HttpRig.JsonAsync(read);
        Assert.Equal(id, readBody.RootElement.GetProperty("id").GetGuid());

        var list = await rig.GetAsync("/chronoaudit/saved-reports", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = await HttpRig.JsonAsync(list);
        Assert.Contains(listBody.RootElement.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);

        var updatePayload = new
        {
            title = "Renamed Activity Overview",
            definition = SampleQuery(),
            expectedStamp = stamp,
        };
        var update = await rig.Client.SendAsync(rig.Request(HttpMethod.Put, $"/chronoaudit/saved-reports/{id}", Who.Me("u1"), json: updatePayload));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var updateBody = await HttpRig.JsonAsync(update);
        Assert.Equal("Renamed Activity Overview", updateBody.RootElement.GetProperty("title").GetString());
        var newStamp = updateBody.RootElement.GetProperty("concurrencyStamp").GetGuid();
        Assert.NotEqual(stamp, newStamp);

        var delete = await rig.Client.SendAsync(rig.Request(HttpMethod.Delete, $"/chronoaudit/saved-reports/{id}?expectedStamp={newStamp}", Who.Me("u1")));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var readAgain = await rig.GetAsync($"/chronoaudit/saved-reports/{id}", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.NotFound, readAgain.StatusCode);
        using var notFoundBody = await HttpRig.JsonAsync(readAgain);
        Assert.Equal("SavedReport.NotFound", notFoundBody.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AStranger_CannotReadOrModifySomeoneElsesReport()
    {
        await using var rig = await HttpRig.StartAsync();

        var createPayload = new
        {
            title = "Private Report",
            definition = SampleQuery(),
        };

        var created = await rig.PostAsync("/chronoaudit/saved-reports", Who.Me("u1"), json: createPayload);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();
        var stamp = createBody.RootElement.GetProperty("concurrencyStamp").GetGuid();

        var read = await rig.GetAsync($"/chronoaudit/saved-reports/{id}", Who.Me("u2"));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var readBody = await HttpRig.JsonAsync(read);
        Assert.Equal("SavedReport.NotFound", readBody.RootElement.GetProperty("title").GetString());

        var updatePayload = new
        {
            title = "Hacked Title",
            definition = SampleQuery(),
            expectedStamp = stamp,
        };
        var update = await rig.Client.SendAsync(rig.Request(HttpMethod.Put, $"/chronoaudit/saved-reports/{id}", Who.Me("u2"), json: updatePayload));
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var delete = await rig.Client.SendAsync(rig.Request(HttpMethod.Delete, $"/chronoaudit/saved-reports/{id}?expectedStamp={stamp}", Who.Me("u2")));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task SharedReport_IsVisibleToGroupAndAdmin_AndViewerCannotModify()
    {
        await using var rig = await HttpRig.StartAsync();

        var createPayload = new
        {
            title = "Group Report",
            definition = SampleQuery(),
            sharedWithGroupId = "g1",
        };

        var created = await rig.PostAsync("/chronoaudit/saved-reports", Who.InGroup("u1", "g1"), json: createPayload);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();
        var stamp = createBody.RootElement.GetProperty("concurrencyStamp").GetGuid();

        var peer = await rig.GetAsync($"/chronoaudit/saved-reports/{id}", Who.InGroup("u2", "g1"));
        Assert.Equal(HttpStatusCode.OK, peer.StatusCode);

        var admin = await rig.GetAsync($"/chronoaudit/saved-reports/{id}", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        var otherGroup = await rig.GetAsync($"/chronoaudit/saved-reports/{id}", Who.InGroup("u3", "g2"));
        Assert.Equal(HttpStatusCode.NotFound, otherGroup.StatusCode);

        var updatePayload = new
        {
            title = "Peer Edit",
            definition = SampleQuery(),
            expectedStamp = stamp,
        };
        var peerEdit = await rig.Client.SendAsync(rig.Request(HttpMethod.Put, $"/chronoaudit/saved-reports/{id}", Who.InGroup("u2", "g1"), json: updatePayload));
        Assert.Equal(HttpStatusCode.Forbidden, peerEdit.StatusCode);
    }

    [SqliteDecimalFact]
    public async Task Running_UsesSavedDefinition_AndOptionalPeriodOverride()
    {
        await using var rig = await HttpRig.StartAsync();
        await rig.GetAsync("/api/hello", Who.Me("u1"));

        var createPayload = new
        {
            title = "Runnable Report",
            definition = SampleQuery(),
        };

        var created = await rig.PostAsync("/chronoaudit/saved-reports", Who.Me("u1"), json: createPayload);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();

        var runDefault = await rig.PostAsync($"/chronoaudit/saved-reports/{id}/run", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.OK, runDefault.StatusCode);
        using var defaultBody = await HttpRig.JsonAsync(runDefault);
        Assert.True(defaultBody.RootElement.GetProperty("rows").GetArrayLength() > 0);

        var runOverride = await rig.PostAsync(
            $"/chronoaudit/saved-reports/{id}/run",
            Who.Me("u1"),
            json: new { from = HttpRig.T0.AddDays(-2).ToString("O"), to = HttpRig.T0.AddDays(2).ToString("O") });
        Assert.Equal(HttpStatusCode.OK, runOverride.StatusCode);

        var runStranger = await rig.PostAsync($"/chronoaudit/saved-reports/{id}/run", Who.Me("u2"));
        Assert.Equal(HttpStatusCode.NotFound, runStranger.StatusCode);
        using var strangerBody = await HttpRig.JsonAsync(runStranger);
        Assert.Equal("SavedReport.NotFound", strangerBody.RootElement.GetProperty("title").GetString());
    }
}
