using System.Net;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class ExportEndpointTests
{
    private static object ActivityParams() => new
    {
        from = HttpRig.T0.AddDays(-1).ToString("O"),
        to = HttpRig.T0.AddDays(1).ToString("O"),
    };

    [SqliteDecimalFact]
    public async Task AJob_IsQueued_RunByWorker_AndDownloadedWithTheSha256Header()
    {
        await using var rig = await HttpRig.StartAsync();
        await rig.GetAsync("/api/hello", Who.Me("u1"));

        var requestPayload = new
        {
            producer = "activity",
            format = "Csv",
            parameters = ActivityParams(),
        };

        var created = await rig.PostAsync("/chronoaudit/exports", Who.Me("u1"), json: requestPayload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Pending", createBody.RootElement.GetProperty("state").GetString());

        await rig.RunExportsAsync();

        var job = await rig.GetAsync($"/chronoaudit/exports/{id}", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        using var jobBody = await HttpRig.JsonAsync(job);
        Assert.Equal("Completed", jobBody.RootElement.GetProperty("state").GetString());
        var sha256 = jobBody.RootElement.GetProperty("sha256").GetString();
        Assert.NotNull(sha256);

        var download = await rig.GetAsync($"/chronoaudit/exports/{id}/download", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(sha256, download.Headers.GetValues(ChronoAuditHttpHeaders.ContentSha256).FirstOrDefault());
        Assert.Equal("text/csv; charset=utf-8", download.Content.Headers.ContentType?.ToString());

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.True(bytes.Length > 3);

        // The header and the job field are only the same claim; the bytes on the wire must hash to it.
        Assert.Equal(sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), ignoreCase: true);
        Assert.Equal(jobBody.RootElement.GetProperty("fileSizeBytes").GetInt64(), bytes.Length);

        Assert.Contains(await rig.AuditRowsAsync(), a => a.Action == AuditActions.ExportDownloaded && a.ActorUserId == "u1");
    }

    [Fact]
    public async Task AJob_IsPrivateToItsRequester()
    {
        await using var rig = await HttpRig.StartAsync();

        var requestPayload = new
        {
            producer = "activity",
            format = "Csv",
            parameters = ActivityParams(),
        };

        var created = await rig.PostAsync("/chronoaudit/exports", Who.Me("u1"), json: requestPayload);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();

        var read = await rig.GetAsync($"/chronoaudit/exports/{id}", Who.Me("u2"));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        var cancel = await rig.PostAsync($"/chronoaudit/exports/{id}/cancel", Who.Me("u2"));
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);

        var download = await rig.GetAsync($"/chronoaudit/exports/{id}/download", Who.Me("u2"));
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task ACancelledJob_TransitionsToCancelled()
    {
        await using var rig = await HttpRig.StartAsync();

        var requestPayload = new
        {
            producer = "activity",
            format = "Csv",
            parameters = ActivityParams(),
        };

        var created = await rig.PostAsync("/chronoaudit/exports", Who.Me("u1"), json: requestPayload);
        using var createBody = await HttpRig.JsonAsync(created);
        var id = createBody.RootElement.GetProperty("id").GetGuid();

        var cancel = await rig.PostAsync($"/chronoaudit/exports/{id}/cancel", Who.Me("u1"));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var cancelBody = await HttpRig.JsonAsync(cancel);
        Assert.Equal("Cancelled", cancelBody.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AnUnknownProducer_OrBadInput_IsRefused()
    {
        await using var rig = await HttpRig.StartAsync();

        var unknown = await rig.PostAsync("/chronoaudit/exports", Who.Me("u1"), json: new { producer = "nope", format = "Csv" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var unknownBody = await HttpRig.JsonAsync(unknown);
        Assert.Equal("Export.UnknownProducer", unknownBody.RootElement.GetProperty("title").GetString());

        var blank = await rig.PostAsync("/chronoaudit/exports", Who.Me("u1"), json: new { producer = "  ", format = "Csv" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        using var blankBody = await HttpRig.JsonAsync(blank);
        Assert.Equal("Export.UnknownProducer", blankBody.RootElement.GetProperty("title").GetString());
    }
}

