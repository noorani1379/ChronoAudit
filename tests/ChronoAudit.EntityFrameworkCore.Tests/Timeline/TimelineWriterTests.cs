using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

internal static class TimelineRig
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public static Task<ActivityHost> HostAsync(Action<ChronoAuditBuilder>? more = null, Action<ChronoAuditOptions>? configure = null)
        => ActivityHost.CreateAsync(configure: configure, customize: b =>
        {
            b.AddBuiltInAnalytics().AddTimeline();
            more?.Invoke(b);
        });

    public static ITimelineWriter Writer(ActivityHost host) => host.Provider.GetRequiredService<ITimelineWriter>();

    public static TimelineEventInput Event(
        int number = 1, string type = "order.paid", string subjectType = "Order", string subjectId = "o-1", DateTimeOffset? at = null, string? group = null,
        string? actor = "u1", string payload = """{"amount":10}""", string? onBehalfOf = null, string? access = null, bool backfilled = false,
        IReadOnlyList<TimelineParticipantInput>? participants = null)
        => new(
            Guid.Parse("00000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)), type, subjectType, subjectId,
            at ?? T0, "Alice", payload, group, actor, "clerk", onBehalfOf, access, null, 1, backfilled, participants);

    public static Task<List<TimelineEvent>> EventsAsync(ActivityHost host)
        => host.ReadStoreAsync(db => db.Set<TimelineEvent>().AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    public static Task<List<TimelineParticipant>> ParticipantsAsync(ActivityHost host)
        => host.ReadStoreAsync(db => db.Set<TimelineParticipant>().AsNoTracking().OrderBy(p => p.GroupKey).ThenBy(p => p.UserId).ThenBy(p => p.Role).ToListAsync());
}

public sealed class TimelineWriterTests
{
    private static readonly IReadOnlyList<TimelineParticipantInput> Alice = [new("alice", "initiator")];

    [Fact]
    public async Task RecordingTwice_StoresOneEvent()
    {
        await using var host = await TimelineRig.HostAsync();
        var writer = TimelineRig.Writer(host);

        var first = await writer.RecordAsync(TimelineRig.Event(), CancellationToken.None);
        var second = await writer.RecordAsync(TimelineRig.Event() with { EventType = "order.refunded" }, CancellationToken.None);

        Assert.True(first.IsSuccess && second.IsSuccess);
        var stored = Assert.Single(await TimelineRig.EventsAsync(host));
        Assert.Equal("order.paid", stored.EventType);
    }

    [Fact]
    public async Task TheEvent_IsStoredInUtc_WithTheCalendarDate_TheRecordTime_AndThePayloadAsSent()
    {
        await using var host = await TimelineRig.HostAsync();
        host.Clock.Set(TimelineRig.T0.AddMinutes(5));
        var local = new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.FromHours(3.5));

        await TimelineRig.Writer(host).RecordAsync(TimelineRig.Event(at: local, payload: """{ "nationalId" : "123" }"""), CancellationToken.None);

        var stored = Assert.Single(await TimelineRig.EventsAsync(host));
        Assert.Equal(local.ToUniversalTime(), stored.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, stored.OccurredAtUtc.Offset);
        Assert.Equal(20261101, stored.CalendarDate);
        Assert.Equal(TimelineRig.T0.AddMinutes(5), stored.RecordedAtUtc);
        Assert.Equal("""{ "nationalId" : "123" }""", stored.Payload);
    }

    [Fact]
    public async Task TheStore_WritesTheEventAndItsParticipants_AndARedeliveryChangesNothing()
    {
        await using var host = await TimelineRig.HostAsync();
        var writer = TimelineRig.Writer(host);
        var input = TimelineRig.Event(group: "case-1", participants: [new("alice", "initiator"), new("bob", "actor")]);

        await writer.RecordAsync(input, CancellationToken.None);
        await writer.RecordAsync(input with { Participants = [new("carol", "assignee")] }, CancellationToken.None);

        Assert.Single(await TimelineRig.EventsAsync(host));
        Assert.Equal(
            new[] { ("alice", "initiator"), ("bob", "actor") },
            (await TimelineRig.ParticipantsAsync(host)).Select(p => (p.UserId, p.Role)));
    }

    [Fact]
    public async Task ASecondEventOfTheSameActor_AddsTheEvent_ButNotTheParticipantAgain()
    {
        await using var host = await TimelineRig.HostAsync();
        var writer = TimelineRig.Writer(host);

        await writer.RecordAsync(TimelineRig.Event(1, group: "case-1", participants: Alice), CancellationToken.None);
        await writer.RecordAsync(TimelineRig.Event(2, group: "case-1", participants: [new("alice", "initiator"), new("alice", "actor")]), CancellationToken.None);

        Assert.Equal(2, (await TimelineRig.EventsAsync(host)).Count);
        Assert.Equal(new[] { ("alice", "actor"), ("alice", "initiator") }, (await TimelineRig.ParticipantsAsync(host)).Select(p => (p.UserId, p.Role)));
    }

    [Fact]
    public async Task OutOfOrderDelivery_GivesTheSameRows()
    {
        await using var inOrder = await TimelineRig.HostAsync();
        await using var reversed = await TimelineRig.HostAsync();
        var events = new[]
        {
            TimelineRig.Event(1, at: TimelineRig.T0, group: "g", participants: [new("alice", "initiator")]),
            TimelineRig.Event(2, at: TimelineRig.T0.AddHours(1), group: "g", participants: [new("alice", "initiator"), new("bob", "actor")]),
            TimelineRig.Event(3, at: TimelineRig.T0.AddHours(2), group: "g", participants: [new("bob", "actor")]),
        };

        foreach (var e in events)
        {
            await TimelineRig.Writer(inOrder).RecordAsync(e, CancellationToken.None);
        }

        foreach (var e in events.Reverse())
        {
            await TimelineRig.Writer(reversed).RecordAsync(e, CancellationToken.None);
        }

        Assert.Equal(
            (await TimelineRig.EventsAsync(inOrder)).Select(e => e.SourceEventId).Order(),
            (await TimelineRig.EventsAsync(reversed)).Select(e => e.SourceEventId).Order());
        Assert.Equal(
            (await TimelineRig.ParticipantsAsync(inOrder)).Select(p => (p.GroupKey, p.UserId, p.Role)),
            (await TimelineRig.ParticipantsAsync(reversed)).Select(p => (p.GroupKey, p.UserId, p.Role)));
    }

    [Fact]
    public async Task ParticipantsWithoutAGroupKey_AreIgnored_AndABackfilledEventIsMarked()
    {
        await using var host = await TimelineRig.HostAsync();
        var writer = TimelineRig.Writer(host);

        await writer.RecordAsync(TimelineRig.Event(1, participants: Alice), CancellationToken.None);
        await writer.RecordAsync(TimelineRig.Event(2, backfilled: true), CancellationToken.None);

        Assert.Empty(await TimelineRig.ParticipantsAsync(host));
        Assert.Equal(new[] { false, true }, (await TimelineRig.EventsAsync(host)).Select(e => e.IsBackfilled));
    }

    [Fact]
    public async Task AnInvalidEvent_IsRefusedWithTheStableCode_AndNothingIsStored()
    {
        await using var host = await TimelineRig.HostAsync();
        var writer = TimelineRig.Writer(host);
        var bad = new[]
        {
            TimelineRig.Event() with { SourceEventId = Guid.Empty },
            TimelineRig.Event(type: new string('x', 129)),
            TimelineRig.Event(subjectType: new string('x', 129)),
            TimelineRig.Event(subjectId: " "),
            TimelineRig.Event(payload: "{not json"),
            TimelineRig.Event(payload: "\"" + new string('x', 32_000) + "\""),
            TimelineRig.Event() with { ActorDisplayName = new string('x', 201) },
            TimelineRig.Event(actor: new string('u', 65)),
            TimelineRig.Event(group: "g", participants: [new("", "r")]),
            TimelineRig.Event(group: "g", participants: [new("u", new string('r', 65))]),
        };

        foreach (var input in bad)
        {
            var result = await writer.RecordAsync(input, CancellationToken.None);
            Assert.Equal(("Timeline.InvalidEvent", ChronoAuditErrorKind.Validation), (result.Error.Code, result.Error.Kind));
        }

        Assert.Empty(await TimelineRig.EventsAsync(host));
        Assert.Empty(await TimelineRig.ParticipantsAsync(host));
    }

    [Fact]
    public async Task Stage_AddsTheEventToTheCallersContext_AndItIsSavedWithTheCallersSave()
    {
        await using var host = await TimelineRig.HostAsync();
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();

        TimelineRig.Writer(host).Stage(db, TimelineRig.Event(7));
        Assert.Empty(await TimelineRig.EventsAsync(host));
        await db.SaveChangesAsync();

        Assert.Single(await TimelineRig.EventsAsync(host));
        Assert.Throws<ArgumentException>(() => TimelineRig.Writer(host).Stage(db, TimelineRig.Event(8, payload: "nope")));
    }

    [Fact]
    public async Task AStagedDuplicate_FailsTheCallersSave()
    {
        await using var host = await TimelineRig.HostAsync();
        await TimelineRig.Writer(host).RecordAsync(TimelineRig.Event(5), CancellationToken.None);
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
        TimelineRig.Writer(host).Stage(db, TimelineRig.Event(5));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
