using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class EvidenceTests
{
    private static readonly DateTimeOffset T0 = TimelineRig.T0;
    private static readonly ChronoAuditScope Admin = ChronoAuditScope.Everything("admin");

    private static IEvidenceService Evidence(ActivityHost host) => host.Provider.GetRequiredService<IEvidenceService>();

    // An order with its audit row, and the timeline events about it.
    private static async Task<(Guid Id, string Table)> OrderWithHistoryAsync(ActivityHost host, bool hash = true)
    {
        Guid id;
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            var order = new Order { Title = "chair", Secret = "topsecret" };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            order.Title = "armchair";
            await db.SaveChangesAsync();
            id = order.Id;
        }

        var table = (await host.AuditRowsAsync())[0].ResourceType;
        if (hash)
        {
            using var scope = host.Provider.CreateScope();
            await host.Provider.GetRequiredService<AuditChain>().HashAllPendingAsync(scope.ServiceProvider.GetRequiredService<ShopDb>(), 500, CancellationToken.None, "shop");
        }

        var writer = TimelineRig.Writer(host);
        await writer.RecordAsync(TimelineRig.Event(1, type: "order.created", subjectType: table, subjectId: id.ToString("D"), at: T0), CancellationToken.None);
        await writer.RecordAsync(TimelineRig.Event(2, type: "order.paid", subjectType: table, subjectId: id.ToString("D"), at: T0.AddMinutes(5)), CancellationToken.None);
        return (id, table);
    }

    private static Task<ActivityHost> HostAsync(Action<ChronoAuditBuilder>? more = null, bool withModule = true)
        => ActivityHost.CreateAsync(withAuditedModule: withModule, customize: b =>
        {
            b.AddTimeline();
            b.Services.AddSingleton<ShopDbConnectionAccess>();
            more?.Invoke(b);
        });

    [Fact]
    public async Task ARecordWithAGeneratedIntKey_HasItsAuditRowsInTheBundle()
    {
        await using var host = await HostAsync();
        int id;
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            var ticket = new Ticket { Title = "open" };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticket.Title = "closed";
            await db.SaveChangesAsync();
            id = ticket.Id;
        }

        var result = await Evidence(host).BuildAsync("Tickets", id.ToString(System.Globalization.CultureInfo.InvariantCulture), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(new[] { AuditActions.AutoAdded, AuditActions.AutoModified }, result.Value.AuditRows.Select(r => r.Action).ToArray());

        // Another record's key finds nothing.
        var other = await Evidence(host).BuildAsync("Tickets", (id + 1000).ToString(System.Globalization.CultureInfo.InvariantCulture), Admin, CancellationToken.None);
        Assert.True(other.IsFailure);
    }

    [Fact]
    public async Task TheBundle_HasTimelineAuditAndVerification()
    {
        await using var host = await HostAsync();
        var (id, table) = await OrderWithHistoryAsync(host);

        var result = await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var bundle = result.Value;
        Assert.Equal((table, id.ToString("D")), (bundle.SubjectType, bundle.SubjectId));
        Assert.Equal(new[] { "order.paid", "order.created" }, bundle.Timeline.Select(t => t.EventType));
        Assert.Equal(2, bundle.AuditRows.Count);
        Assert.All(bundle.AuditRows, r => Assert.NotNull(r.PayloadHash));
        var check = Assert.Single(bundle.ChainChecks);
        Assert.Equal("shop", check.Module);
        Assert.True(check.Verification.IsValid);
        Assert.Equal((2L, 0L), (check.Verification.VerifiedCount, check.Verification.PendingCount));
        Assert.Equal(64, bundle.DataSha256.Length);
        Assert.Equal(host.Clock.GetUtcNow(), bundle.GeneratedAtUtc);
        Assert.DoesNotContain("topsecret", string.Concat(bundle.AuditRows.Select(r => r.NewValues)), StringComparison.Ordinal);
        Assert.Contains("***MASKED***", string.Concat(bundle.AuditRows.Select(r => r.NewValues)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowsThatAreNotHashedYet_AreReportedAsPending()
    {
        await using var host = await HostAsync();
        var (id, table) = await OrderWithHistoryAsync(host, hash: false);

        var bundle = (await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None)).Value;

        var check = Assert.Single(bundle.ChainChecks);
        Assert.True(check.Verification.IsValid);
        Assert.Equal((0L, 2L), (check.Verification.VerifiedCount, check.Verification.PendingCount));
    }

    [Fact]
    public async Task ATamperedAuditRow_ShowsInTheVerificationOfTheBundle()
    {
        await using var host = await HostAsync();
        var (id, table) = await OrderWithHistoryAsync(host);
        var connection = host.Provider.GetRequiredService<ShopDbConnectionAccess>();
        await connection.TamperAsync("UPDATE AuditLogEntries SET Action = 'forged' WHERE SequenceNumber = 1");

        var bundle = (await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None)).Value;

        Assert.False(Assert.Single(bundle.ChainChecks).Verification.IsValid);
    }

    [Fact]
    public async Task TheDataHash_IsStableForTheSameEvents_AndChangesWithAnyEventOrItsOrder()
    {
        await using var host = await HostAsync();
        var (id, table) = await OrderWithHistoryAsync(host);
        var first = (await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None)).Value;
        host.Clock.Advance(TimeSpan.FromHours(1));
        var second = (await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None)).Value;

        var reordered = EvidenceHash.Compute(first.Timeline);
        var oldestFirst = EvidenceHash.Compute(first.Timeline.Reverse());
        var changed = first.Timeline.Select((t, i) => i == 0 ? t with { Payload = """{"amount":11}""" } : t).Reverse();
        await TimelineRig.Writer(host).RecordAsync(TimelineRig.Event(9, type: "order.shipped", subjectType: table, subjectId: id.ToString("D"), at: T0.AddHours(2)), CancellationToken.None);
        var third = (await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None)).Value;

        Assert.Equal(first.DataSha256, second.DataSha256);
        Assert.Equal(oldestFirst, first.DataSha256);
        Assert.NotEqual(reordered, first.DataSha256);
        Assert.NotEqual(EvidenceHash.Compute(changed), first.DataSha256);
        Assert.NotEqual(first.DataSha256, third.DataSha256);
        Assert.Equal(first.DataSha256, first.DataSha256.ToUpperInvariant());
    }

    [Fact]
    public async Task EveryIssue_IsAuditedBeforeTheBundleIsReturned()
    {
        await using var host = await HostAsync();
        var (id, table) = await OrderWithHistoryAsync(host);

        var bundle = (await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None)).Value;
        await Evidence(host).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None);

        var issued = (await host.AuditRowsAsync()).Where(a => a.Action == AuditActions.EvidenceIssued).ToList();
        Assert.Equal(2, issued.Count);
        Assert.All(issued, a => Assert.Equal(("admin", table, id.ToString("D")), (a.ActorUserId, a.ResourceType, a.ResourceId)));
        Assert.Contains(bundle.DataSha256, issued[0].Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheIssueCannotBeRecorded_NoDocumentIsReturned()
    {
        await using var failing = await HostAsync(b => b.Services.AddScoped<IAuditLogWriter, ThrowingAuditWriter>());
        var (id, table) = await OrderWithHistoryAsync(failing);
        await using var noModule = await HostAsync(withModule: false);
        await TimelineRig.Writer(noModule).RecordAsync(TimelineRig.Event(1, subjectType: "Order", subjectId: "o-1"), CancellationToken.None);

        var down = await Evidence(failing).BuildAsync(table, id.ToString("D"), Admin, CancellationToken.None);
        var none = await Evidence(noModule).BuildAsync("Order", "o-1", Admin, CancellationToken.None);

        Assert.Equal(("ChronoAudit.AuditUnavailable", ChronoAuditErrorKind.Unavailable), (down.Error.Code, down.Error.Kind));
        Assert.Equal("ChronoAudit.AuditUnavailable", none.Error.Code);
    }

    [Fact]
    public async Task MoreThanTheCapOfEvents_IsRefused_WithTheStableCode()
    {
        await using var host = await HostAsync();
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
            db.Set<TimelineEvent>().AddRange(Enumerable.Range(1, IEvidenceService.MaxItems + 1).Select(i => new TimelineEvent
            {
                SourceEventId = Guid.NewGuid(),
                EventType = "bulk",
                SubjectType = "Order",
                SubjectId = "big",
                OccurredAtUtc = T0.AddSeconds(i),
                RecordedAtUtc = T0,
                ActorDisplayName = "system",
                Payload = "{}",
            }));
            await db.SaveChangesAsync();
        }

        var result = await Evidence(host).BuildAsync("Order", "big", Admin, CancellationToken.None);

        Assert.Equal(("Evidence.TooLarge", ChronoAuditErrorKind.Validation), (result.Error.Code, result.Error.Kind));
        Assert.DoesNotContain(await host.AuditRowsAsync(), a => a.Action == AuditActions.EvidenceIssued);
    }

    [Fact]
    public async Task AnUnknownOrOutOfScopeSubject_IsNotFound_WithNothingRecorded()
    {
        await using var host = await HostAsync();
        var (id, table) = await OrderWithHistoryAsync(host);

        var unknown = await Evidence(host).BuildAsync(table, Guid.NewGuid().ToString("D"), Admin, CancellationToken.None);
        var own = await Evidence(host).BuildAsync(table, id.ToString("D"), ChronoAuditScope.OwnOf("admin"), CancellationToken.None);
        var group = await Evidence(host).BuildAsync(table, id.ToString("D"), ChronoAuditScope.GroupOf("admin", "g"), CancellationToken.None);
        var blank = await Evidence(host).BuildAsync(" ", "x", Admin, CancellationToken.None);

        Assert.All(new[] { unknown, own, group, blank }, r => Assert.Equal(("Evidence.NotFound", ChronoAuditErrorKind.NotFound), (r.Error.Code, r.Error.Kind)));
        Assert.DoesNotContain(await host.AuditRowsAsync(), a => a.Action == AuditActions.EvidenceIssued);
    }
}

/// <summary>Lets a test change a stored audit row behind the application's back (the guard triggers are not installed in these tests).</summary>
public sealed class ShopDbConnectionAccess(IServiceScopeFactory scopes)
{
    public async Task TamperAsync(string sql)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(sql);
#pragma warning restore EF1002
    }
}
