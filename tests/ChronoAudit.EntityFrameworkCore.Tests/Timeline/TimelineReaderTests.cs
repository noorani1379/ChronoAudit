using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class TimelineReaderTests
{
    private static readonly DateTimeOffset T0 = TimelineRig.T0;

    private static ITimelineReader Reader(ActivityHost host) => host.Provider.GetRequiredService<ITimelineReader>();

    private static readonly ChronoAuditScope All = ChronoAuditScope.Everything("admin");

    private sealed class UpperTitles : ITimelineTitleProvider
    {
        public string? TitleOf(string eventType, int schemaVersion, string payload) => eventType == "order.paid" ? "Order PAID v" + schemaVersion : null;
    }

    private static async Task RecordAsync(ActivityHost host, params TimelineEventInput[] events)
    {
        foreach (var e in events)
        {
            Assert.True((await TimelineRig.Writer(host).RecordAsync(e, CancellationToken.None)).IsSuccess);
        }
    }

    [Fact]
    public async Task SubjectGroupAndActorTimelines_ArePagedAndBounded()
    {
        await using var host = await TimelineRig.HostAsync();
        for (var i = 1; i <= 5; i++)
        {
            await RecordAsync(host, TimelineRig.Event(i, at: T0.AddMinutes(i), group: "case-1", subjectId: "o-1", actor: "alice"));
        }

        await RecordAsync(host, TimelineRig.Event(6, at: T0.AddMinutes(6), group: "case-1", subjectType: "Invoice", subjectId: "i-1", actor: "bob"));
        await RecordAsync(host, TimelineRig.Event(7, at: T0.AddMinutes(7), subjectId: "o-2", actor: "alice"));

        var subject = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Limit: 2, Before: cursor), All, CancellationToken.None)).Value;
            Assert.True(page.Items.Count <= 2);
            subject.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        var group = (await Reader(host).OfGroupAsync("case-1", new TimelineFilter(), All, CancellationToken.None)).Value;
        var actor = (await Reader(host).OfActorAsync("alice", new TimelineFilter(), All, CancellationToken.None)).Value;

        Assert.Equal(3, pages);
        Assert.Equal(5, subject.Count);
        Assert.Equal(subject.OrderByDescending(x => x), subject);
        Assert.Equal(6, group.Items.Count);
        Assert.Equal(T0.AddMinutes(6), group.Items[0].OccurredAtUtc);
        Assert.Equal(6, actor.Items.Count);
        Assert.Equal(T0.AddMinutes(7), actor.Items[0].OccurredAtUtc);
        Assert.Null(group.NextCursor);
    }

    [Fact]
    public async Task EventsWithTheSameTime_KeepAStableOrder_AcrossPages()
    {
        await using var host = await TimelineRig.HostAsync();
        for (var i = 1; i <= 5; i++)
        {
            await RecordAsync(host, TimelineRig.Event(i, at: T0));
        }

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Limit: 2, Before: cursor), All, CancellationToken.None)).Value;
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(x => x), seen);
    }

    [Fact]
    public async Task TheFilter_NarrowsByEventTypeAndPeriod()
    {
        await using var host = await TimelineRig.HostAsync();
        await RecordAsync(
            host,
            TimelineRig.Event(1, type: "order.created", at: T0),
            TimelineRig.Event(2, type: "order.paid", at: T0.AddDays(1)),
            TimelineRig.Event(3, type: "order.shipped", at: T0.AddDays(2)));

        var types = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(EventTypes: ["order.paid", "order.shipped"]), All, CancellationToken.None)).Value;
        var period = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(From: T0.AddHours(1), To: T0.AddDays(1)), All, CancellationToken.None)).Value;

        Assert.Equal(new[] { "order.shipped", "order.paid" }, types.Items.Select(i => i.EventType));
        Assert.Equal(new[] { "order.paid" }, period.Items.Select(i => i.EventType));
    }

    [Fact]
    public async Task TheScope_IsAppliedInsideTheQuery()
    {
        await using var host = await TimelineRig.HostAsync();
        await RecordAsync(
            host,
            TimelineRig.Event(1, actor: "alice", access: "gA"),
            TimelineRig.Event(2, actor: "bob", access: "gA"),
            TimelineRig.Event(3, actor: "carol", access: "gB"),
            TimelineRig.Event(4, actor: "dave", onBehalfOf: "alice", access: "gB"),
            TimelineRig.Event(5, actor: "erin", group: "case-9", access: "gB", participants: [new("alice", "assignee")]),
            TimelineRig.Event(6, actor: null, access: null));

        async Task<List<int>> Numbers(ChronoAuditScope scope)
        {
            var page = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(), scope, CancellationToken.None)).Value;
            var numberOf = (await TimelineRig.EventsAsync(host)).ToDictionary(e => e.Id, e => int.Parse(e.SourceEventId.ToString("N")[^12..], System.Globalization.CultureInfo.InvariantCulture));
            return page.Items.Select(i => numberOf[i.Id]).Order().ToList();
        }

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, await Numbers(All));
        Assert.Equal(new[] { 1, 2 }, await Numbers(ChronoAuditScope.GroupOf("zed", "gA")));
        Assert.Equal(new[] { 1, 2 }, await Numbers(ChronoAuditScope.GroupOf("alice", "gA")));
        Assert.Equal(new[] { 1, 3, 4, 5 }, await Numbers(ChronoAuditScope.GroupOf("alice", "gB")));
        Assert.Equal(new[] { 1, 4, 5 }, await Numbers(ChronoAuditScope.OwnOf("alice")));
        Assert.Empty(await Numbers(ChronoAuditScope.OwnOf("nobody")));
        Assert.Empty(await Numbers(new ChronoAuditScope(ScopeKind.Own, null, null)));
        Assert.Empty(await Numbers(new ChronoAuditScope(ScopeKind.Group, null, null)));
        Assert.Equal(new[] { 1, 2 }, await Numbers(new ChronoAuditScope(ScopeKind.Group, null, "gA")));
    }


    [Fact]
    public async Task PrivateFields_AreMaskedWhenRead_AndTheStoredPayloadIsNeverChanged()
    {
        await using var host = await TimelineRig.HostAsync(configure: o => o.PersonalData.NumberFields.Add("nationalId"));
        const string payload = """{"nationalId":"0012345678","password":"s3cret","amount":10}""";
        await RecordAsync(host, TimelineRig.Event(payload: payload));

        var item = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(), All, CancellationToken.None)).Value.Items.Single();

        Assert.DoesNotContain("0012345678", item.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", item.Payload, StringComparison.Ordinal);
        Assert.Contains("\"amount\":10", item.Payload, StringComparison.Ordinal);
        Assert.Equal(payload, Assert.Single(await TimelineRig.EventsAsync(host)).Payload);
    }

    [Fact]
    public async Task Titles_ComeFromTheHostProvider_OrTheEventType()
    {
        await using var withProvider = await TimelineRig.HostAsync(b => b.Services.AddSingleton<ITimelineTitleProvider, UpperTitles>());
        await using var without = await TimelineRig.HostAsync();
        foreach (var host in new[] { withProvider, without })
        {
            await RecordAsync(host, TimelineRig.Event(1, type: "order.paid", at: T0), TimelineRig.Event(2, type: "order.other", at: T0.AddMinutes(1)));
        }

        var titled = (await Reader(withProvider).OfSubjectAsync("Order", "o-1", new TimelineFilter(), All, CancellationToken.None)).Value.Items;
        var plain = (await Reader(without).OfSubjectAsync("Order", "o-1", new TimelineFilter(), All, CancellationToken.None)).Value.Items;

        Assert.Equal(new[] { "order.other", "Order PAID v1" }, titled.Select(i => i.Title));
        Assert.Equal(new[] { "order.other", "order.paid" }, plain.Select(i => i.Title));
    }

    [Fact]
    public async Task TheTimelineOfAUser_ListsEveryEventTheyTookPartIn_NewestFirst()
    {
        await using var host = await TimelineRig.HostAsync();
        await RecordAsync(
            host,
            TimelineRig.Event(1, at: T0, actor: "alice"),
            TimelineRig.Event(2, at: T0.AddMinutes(1), actor: "bob", group: "case-1", participants: [new("alice", "initiator")]),
            TimelineRig.Event(3, at: T0.AddMinutes(2), actor: "carol", onBehalfOf: "alice"),
            TimelineRig.Event(4, at: T0.AddMinutes(3), actor: "dave"),
            TimelineRig.Event(5, at: T0.AddMinutes(4), actor: "erin", group: "case-1"));

        var page = (await Reader(host).OfActorAsync("alice", new TimelineFilter(), All, CancellationToken.None)).Value;

        Assert.Equal(new[] { T0.AddMinutes(4), T0.AddMinutes(2), T0.AddMinutes(1), T0 }, page.Items.Select(i => i.OccurredAtUtc));
    }

    [Fact]
    public async Task ATamperedCursor_AnEmptyUser_AnOversizedPage_AndBadFilters_AreRefused()
    {
        await using var host = await TimelineRig.HostAsync();
        await RecordAsync(host, TimelineRig.Event(1), TimelineRig.Event(2, at: T0.AddMinutes(1)));
        var good = (await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Limit: 1), All, CancellationToken.None)).Value.NextCursor!;
        var worked = await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Before: good), All, CancellationToken.None);

        var tampered = await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Before: CursorCodec.Encode("t1:", "abc:5")), All, CancellationToken.None);
        var garbage = await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Before: "not-a-cursor"), All, CancellationToken.None);
        var foreign = await Reader(host).OfSubjectAsync("Order", "o-1", new TimelineFilter(Before: Convert.ToBase64String("x1:5:5"u8.ToArray())), All, CancellationToken.None);
        var noUser = await Reader(host).OfActorAsync(" ", new TimelineFilter(), All, CancellationToken.None);
        var big = await Reader(host).OfGroupAsync("g", new TimelineFilter(Limit: 201), All, CancellationToken.None);
        var zero = await Reader(host).OfGroupAsync("g", new TimelineFilter(Limit: 0), All, CancellationToken.None);
        var reversed = await Reader(host).OfGroupAsync("g", new TimelineFilter(From: T0.AddDays(1), To: T0), All, CancellationToken.None);
        var manyTypes = await Reader(host).OfGroupAsync("g", new TimelineFilter(EventTypes: Enumerable.Range(0, 21).Select(i => "t" + i).ToList()), All, CancellationToken.None);
        var noSubject = await Reader(host).OfSubjectAsync("", "x", new TimelineFilter(), All, CancellationToken.None);

        Assert.True(worked.IsSuccess);
        Assert.Single(worked.Value.Items);
        Assert.Equal("ChronoAudit.InvalidCursor", tampered.Error.Code);
        Assert.Equal("ChronoAudit.InvalidCursor", garbage.Error.Code);
        Assert.Equal("ChronoAudit.InvalidCursor", foreign.Error.Code);
        Assert.All(new[] { noUser, big, zero, reversed, manyTypes, noSubject }, r => Assert.Equal("ChronoAudit.Validation", r.Error.Code));
    }
}
