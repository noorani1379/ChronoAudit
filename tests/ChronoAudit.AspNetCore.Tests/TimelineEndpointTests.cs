using System.Net;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class TimelineEndpointTests
{
    [Fact]
    public async Task SubjectGroupAndActor_ReturnTimelineEvents_AndFilterByEventType()
    {
        await using var rig = await HttpRig.StartAsync();
        var writer = rig.Services.GetRequiredService<ITimelineWriter>();

        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "order.created", "Order", "order-1", HttpRig.T0, "Alice", "{}", GroupKey: "g1", ActorUserId: "u1"),
            CancellationToken.None);
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "order.paid", "Order", "order-1", HttpRig.T0.AddMinutes(5), "Alice", "{}", GroupKey: "g1", ActorUserId: "u1"),
            CancellationToken.None);
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "invoice.issued", "Invoice", "inv-1", HttpRig.T0.AddMinutes(10), "Bob", "{}", GroupKey: "g1", ActorUserId: "u2"),
            CancellationToken.None);

        var subject = await rig.GetAsync("/chronoaudit/timeline/subjects/Order/order-1", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, subject.StatusCode);
        using var subjectBody = await HttpRig.JsonAsync(subject);
        Assert.Equal(2, subjectBody.RootElement.GetProperty("items").GetArrayLength());

        var filtered = await rig.GetAsync("/chronoaudit/timeline/subjects/Order/order-1?eventTypes=order.paid", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using var filteredBody = await HttpRig.JsonAsync(filtered);
        var item = Assert.Single(filteredBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("order.paid", item.GetProperty("eventType").GetString());

        var group = await rig.GetAsync("/chronoaudit/timeline/groups/g1", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, group.StatusCode);
        using var groupBody = await HttpRig.JsonAsync(group);
        Assert.Equal(3, groupBody.RootElement.GetProperty("items").GetArrayLength());

        var actor = await rig.GetAsync("/chronoaudit/timeline/actors/u1", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, actor.StatusCode);
        using var actorBody = await HttpRig.JsonAsync(actor);
        Assert.Equal(2, actorBody.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TheEvidenceRoute_ReturnsTheBundle_AndIsAudited()
    {
        await using var rig = await HttpRig.StartAsync();
        var (table, ids) = await Seed.OrdersAsync(rig, 1);
        await Seed.HashAsync(rig);

        var writer = rig.Services.GetRequiredService<ITimelineWriter>();
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "order.placed", table, ids[0].ToString("D"), HttpRig.T0, "Alice", "{}"),
            CancellationToken.None);

        var response = await rig.GetAsync($"/chronoaudit/timeline/subjects/{table}/{ids[0]}/evidence", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);

        Assert.Equal(table, body.RootElement.GetProperty("subjectType").GetString());
        Assert.Equal(ids[0].ToString("D"), body.RootElement.GetProperty("subjectId").GetString());
        Assert.NotNull(body.RootElement.GetProperty("dataSha256").GetString());
        Assert.True(body.RootElement.GetProperty("timeline").GetArrayLength() >= 1);
        Assert.True(body.RootElement.GetProperty("auditRows").GetArrayLength() >= 1);

        var auditRows = await rig.AuditRowsAsync();
        Assert.Contains(auditRows, a => a.Action == AuditActions.EvidenceIssued && a.ActorUserId == "admin");

        var own = await rig.GetAsync($"/chronoaudit/timeline/subjects/{table}/{ids[0]}/evidence", Who.Me());
        Assert.Equal(HttpStatusCode.NotFound, own.StatusCode);

        var unknown = await rig.GetAsync("/chronoaudit/timeline/subjects/Nope/none/evidence", Who.Admin);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static async Task<HashSet<string>> TypesAsync(HttpRig rig, string url, Who who)
    {
        var response = await rig.GetAsync(url, who);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        return body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventType").GetString()!).ToHashSet();
    }

    [Fact]
    public async Task AnOwnScopeCaller_SeesOnlyTheEventsTheyTookPartIn()
    {
        await using var rig = await HttpRig.StartAsync();
        var writer = rig.Services.GetRequiredService<ITimelineWriter>();
        TimelineParticipantInput[] g1 = [new("u1", "buyer"), new("u2", "seller")];

        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "a.u1-acts", "Order", "S1", HttpRig.T0, "U1", "{}", GroupKey: "g1", ActorUserId: "u1", Participants: g1),
            CancellationToken.None);
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "b.u2-acts-in-g1", "Order", "S1", HttpRig.T0.AddMinutes(1), "U2", "{}", GroupKey: "g1", ActorUserId: "u2", Participants: g1),
            CancellationToken.None);
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "c.u2-acts-alone", "Order", "S2", HttpRig.T0.AddMinutes(2), "U2", "{}", GroupKey: "g2", ActorUserId: "u2", Participants: [new("u2", "seller")]),
            CancellationToken.None);

        var me = Who.Me("u1");

        // u1 took part in g1, so both its events are visible; g2 is a stranger's group.
        Assert.Equal(new HashSet<string> { "a.u1-acts", "b.u2-acts-in-g1" }, await TypesAsync(rig, "/chronoaudit/timeline/groups/g1", me));
        Assert.Empty(await TypesAsync(rig, "/chronoaudit/timeline/groups/g2", me));
        Assert.Empty(await TypesAsync(rig, "/chronoaudit/timeline/subjects/Order/S2", me));

        // The timeline of another actor (u2 acts in g1 and g2, and is a participant of both) shows only what the caller may see anyway: g2 stays hidden.
        Assert.Equal(new HashSet<string> { "a.u1-acts", "b.u2-acts-in-g1" }, await TypesAsync(rig, "/chronoaudit/timeline/actors/u2", me));

        // The All scope sees everything.
        Assert.Equal(3, (await TypesAsync(rig, "/chronoaudit/timeline/actors/u2", Who.Admin)).Count);
        Assert.Contains("c.u2-acts-alone", await TypesAsync(rig, "/chronoaudit/timeline/groups/g2", Who.Admin));
    }

    [Fact]
    public async Task AGroupScopeCaller_SeesOnlyTheirAccessGroup()
    {
        await using var rig = await HttpRig.StartAsync();
        var writer = rig.Services.GetRequiredService<ITimelineWriter>();

        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "doc.in-a", "Doc", "d1", HttpRig.T0, "X", "{}", ActorUserId: "u9", AccessGroupId: "grp-A"),
            CancellationToken.None);
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "doc.in-b", "Doc", "d1", HttpRig.T0.AddMinutes(1), "Y", "{}", ActorUserId: "u8", AccessGroupId: "grp-B"),
            CancellationToken.None);
        await writer.RecordAsync(
            new TimelineEventInput(Guid.NewGuid(), "doc.no-group", "Doc", "d1", HttpRig.T0.AddMinutes(2), "Z", "{}", ActorUserId: "u7"),
            CancellationToken.None);

        Assert.Equal(new HashSet<string> { "doc.in-a" }, await TypesAsync(rig, "/chronoaudit/timeline/subjects/Doc/d1", Who.InGroup("u3", "grp-A")));
        Assert.Equal(new HashSet<string> { "doc.in-b" }, await TypesAsync(rig, "/chronoaudit/timeline/subjects/Doc/d1", Who.InGroup("u3", "grp-B")));

        // An event without an access group is nobody's group data: a group caller never sees it (only the All scope does).
        Assert.Equal(3, (await TypesAsync(rig, "/chronoaudit/timeline/subjects/Doc/d1", Who.Admin)).Count);
    }

    [Fact]
    public async Task APage_FollowsTheCursor_NewestFirst_WithoutRepeatsOrGaps()
    {
        await using var rig = await HttpRig.StartAsync();
        var writer = rig.Services.GetRequiredService<ITimelineWriter>();
        for (var i = 0; i < 5; i++)
        {
            await writer.RecordAsync(
                new TimelineEventInput(Guid.NewGuid(), $"step.{i}", "Order", "P1", HttpRig.T0.AddMinutes(i), "A", "{}", ActorUserId: "u1"),
                CancellationToken.None);
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/chronoaudit/timeline/subjects/Order/P1?limit=2" + (cursor is null ? string.Empty : "&before=" + Uri.EscapeDataString(cursor));
            var response = await rig.GetAsync(url, Who.Admin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await HttpRig.JsonAsync(response);
            seen.AddRange(body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventType").GetString()!));
            cursor = body.RootElement.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(["step.4", "step.3", "step.2", "step.1", "step.0"], seen);
    }

    [Theory]
    [InlineData("limit=0", "ChronoAudit.Validation")]
    [InlineData("limit=201", "ChronoAudit.Validation")]
    [InlineData("before=not-a-cursor", "ChronoAudit.InvalidCursor")]
    public async Task ABadLimitOrCursor_IsRefused_WithTheStableCode(string query, string code)
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.GetAsync("/chronoaudit/timeline/subjects/Order/P1?" + query, Who.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal(code, body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AtMostTwentyEventTypes_AreAccepted()
    {
        await using var rig = await HttpRig.StartAsync();
        var twenty = string.Join(',', Enumerable.Range(1, 20).Select(i => "t" + i));
        var twentyOne = twenty + ",t21";

        var ok = await rig.GetAsync("/chronoaudit/timeline/subjects/Order/P1?eventTypes=" + twenty, Who.Admin);
        var tooMany = await rig.GetAsync("/chronoaudit/timeline/subjects/Order/P1?eventTypes=" + twentyOne, Who.Admin);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        using var body = await HttpRig.JsonAsync(tooMany);
        Assert.Equal("ChronoAudit.Validation", body.RootElement.GetProperty("title").GetString());
    }
}
