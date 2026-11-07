using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PostgresTimestampTests
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public async Task EveryWrite_UsesOffsetZero()
    {
        // Non-zero offset (e.g. +03:30)
        var nonUtc = new DateTimeOffset(2026, 11, 1, 14, 30, 0, TimeSpan.FromHours(3.5));

        await using var db = await TestDatabase.CreateAsync(DatabaseProvider.PostgreSql, now: nonUtc);

        // 1. Full save of an audited entity
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            shop.Orders.Add(new Order { Title = "PostgresTzTest", Quantity = 10 });
            await shop.SaveChangesAsync();
        }

        // Verify persisted AuditLogEntry has offset zero
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            var entry = await shop.AuditLogEntries.SingleAsync();
            Assert.Equal(TimeSpan.Zero, entry.OccurredAtUtc.Offset);

            // 2. Hash
            var chain = scope.ServiceProvider.GetRequiredService<AuditChain>();
            var hashed = await chain.HashAllPendingAsync(shop, batchSize: 10, CancellationToken.None);
            Assert.Equal(1, hashed);

            var verify = await chain.VerifyAsync(shop, CancellationToken.None);
            Assert.True(verify.IsValid);
        }

        // 3. Timeline event
        using (var scope = db.Scope())
        {
            var timeline = scope.ServiceProvider.GetRequiredService<ITimelineWriter>();
            var tlResult = await timeline.RecordAsync(
                new TimelineEventInput(
                    SourceEventId: Guid.NewGuid(),
                    EventType: "order.placed",
                    SubjectType: "Order",
                    SubjectId: "123",
                    OccurredAtUtc: nonUtc,
                    ActorDisplayName: "Tester",
                    Payload: "{}"),
                CancellationToken.None);

            Assert.True(tlResult.IsSuccess);

            var store = db.Store(scope);
            var tl = await store.TimelineEvents.SingleAsync();
            Assert.Equal(TimeSpan.Zero, tl.OccurredAtUtc.Offset);
            Assert.Equal(TimeSpan.Zero, tl.RecordedAtUtc.Offset);
        }

        // 4. Export job
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var export = ExportJob.Create(
                requestedByUserId: "user-1",
                producer: "analytics",
                format: ExportFormat.Csv,
                parametersJson: "{}",
                scopeKind: ScopeKind.All,
                scopeGroupId: null,
                now: nonUtc);

            store.ExportJobs.Add(export);
            await store.SaveChangesAsync();

            var saved = await store.ExportJobs.SingleAsync(j => j.Id == export.Id);
            Assert.Equal(TimeSpan.Zero, saved.CreatedAtUtc.Offset);
        }
    }
}

