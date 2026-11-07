using ChronoAudit.EntityFrameworkCore;

namespace ChronoAudit.IntegrationTests;

public abstract class GuardTriggerTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyDeleteOfAuditRow_IsRefusedAsync()
    {
        var db = Fixture.Database;
        long seq;
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            var entry = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = "u1",
                Action = "create",
                ResourceType = "Orders",
                ResourceId = "1",
            };
            shop.AuditLogEntries.Add(entry);
            await shop.SaveChangesAsync();
            seq = entry.SequenceNumber;
        }

        var sql = db.Provider == DatabaseProvider.SqlServer
            ? $"DELETE FROM shop.AuditLogEntries WHERE SequenceNumber = {seq}"
            : $"DELETE FROM shop.\"AuditLogEntries\" WHERE \"SequenceNumber\" = {seq}";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(sql));
        Assert.Contains("cannot be deleted", ex.Message, StringComparison.OrdinalIgnoreCase);

        var countSql = db.Provider == DatabaseProvider.SqlServer
            ? $"SELECT COUNT(*) FROM shop.AuditLogEntries WHERE SequenceNumber = {seq}"
            : $"SELECT COUNT(*) FROM shop.\"AuditLogEntries\" WHERE \"SequenceNumber\" = {seq}";
        var count = await db.ExecuteScalarAsync<int>(countSql);
        Assert.Equal(1, count);
    }

    protected async Task VerifyFirstHashSet_Allowed_SecondHashChange_RefusedAsync()
    {
        var db = Fixture.Database;
        long seq;
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            var entry = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = "u1",
                Action = "create",
                ResourceType = "Orders",
                ResourceId = "2",
            };
            shop.AuditLogEntries.Add(entry);
            await shop.SaveChangesAsync();
            seq = entry.SequenceNumber;
        }

        var firstUpdate = db.Provider == DatabaseProvider.SqlServer
            ? $"UPDATE shop.AuditLogEntries SET PayloadHash = 'AA', PreviousHash = NULL, ChainIndex = 1 WHERE SequenceNumber = {seq}"
            : $"UPDATE shop.\"AuditLogEntries\" SET \"PayloadHash\" = 'AA', \"PreviousHash\" = NULL, \"ChainIndex\" = 1 WHERE \"SequenceNumber\" = {seq}";

        var rows = await db.ExecuteRawAsync(firstUpdate);
        Assert.Equal(1, rows);

        var secondUpdate = db.Provider == DatabaseProvider.SqlServer
            ? $"UPDATE shop.AuditLogEntries SET PayloadHash = 'BB' WHERE SequenceNumber = {seq}"
            : $"UPDATE shop.\"AuditLogEntries\" SET \"PayloadHash\" = 'BB' WHERE \"SequenceNumber\" = {seq}";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(secondUpdate));
        Assert.Contains("only the first hash", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    protected async Task VerifyChangeOfAction_IsRefusedAsync()
    {
        var db = Fixture.Database;
        long seq;
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            var entry = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = "u1",
                Action = "create",
                ResourceType = "Orders",
                ResourceId = "3",
            };
            shop.AuditLogEntries.Add(entry);
            await shop.SaveChangesAsync();
            seq = entry.SequenceNumber;
        }

        var updateSql = db.Provider == DatabaseProvider.SqlServer
            ? $"UPDATE shop.AuditLogEntries SET Action = 'hacked' WHERE SequenceNumber = {seq}"
            : $"UPDATE shop.\"AuditLogEntries\" SET \"Action\" = 'hacked' WHERE \"SequenceNumber\" = {seq}";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(updateSql));
        Assert.True(
            ex.Message.Contains("cannot be modified", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("only the first hash", StringComparison.OrdinalIgnoreCase),
            $"Unexpected error message: {ex.Message}");
    }

    protected async Task VerifyBodyRowAndTimelineEvent_CannotBeUpdatedOrDeletedAsync()
    {
        var db = Fixture.Database;

        // 1. ApiRequestBody
        long logId;
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var log = new ApiRequestLog
            {
                RequestId = Guid.NewGuid(),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                DurationMs = 10,
                HttpMethod = "POST",
                Path = "/api/test",
                StatusCode = 200,
                HasBody = true
            };
            store.ApiRequestLogs.Add(log);
            await store.SaveChangesAsync();
            logId = log.Id;

            var body = new ApiRequestBody
            {
                ApiRequestLogId = logId,
                Body = "original body"
            };
            store.ApiRequestBodies.Add(body);
            await store.SaveChangesAsync();
        }

        var updateBody = db.Provider == DatabaseProvider.SqlServer
            ? $"UPDATE chronoaudit.ApiRequestBodies SET Body = 'tampered' WHERE ApiRequestLogId = {logId}"
            : $"UPDATE chronoaudit.\"ApiRequestBodies\" SET \"Body\" = 'tampered' WHERE \"ApiRequestLogId\" = {logId}";
        var exUpdateBody = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(updateBody));
        Assert.Contains("append-only", exUpdateBody.Message, StringComparison.OrdinalIgnoreCase);

        var deleteBody = db.Provider == DatabaseProvider.SqlServer
            ? $"DELETE FROM chronoaudit.ApiRequestBodies WHERE ApiRequestLogId = {logId}"
            : $"DELETE FROM chronoaudit.\"ApiRequestBodies\" WHERE \"ApiRequestLogId\" = {logId}";
        var exDeleteBody = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(deleteBody));
        Assert.Contains("append-only", exDeleteBody.Message, StringComparison.OrdinalIgnoreCase);

        // 2. TimelineEvent
        long eventId;
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var tl = new TimelineEvent
            {
                SourceEventId = Guid.NewGuid(),
                EventType = "order.created",
                SubjectType = "Order",
                SubjectId = Guid.NewGuid().ToString("N"),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                ActorDisplayName = "Admin",
                Payload = "{}",
            };
            store.TimelineEvents.Add(tl);
            await store.SaveChangesAsync();
            eventId = tl.Id;
        }

        var updateTl = db.Provider == DatabaseProvider.SqlServer
            ? $"UPDATE chronoaudit.TimelineEvents SET Payload = 'tampered' WHERE Id = {eventId}"
            : $"UPDATE chronoaudit.\"TimelineEvents\" SET \"Payload\" = 'tampered' WHERE \"Id\" = {eventId}";
        var exUpdateTl = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(updateTl));
        Assert.Contains("append-only", exUpdateTl.Message, StringComparison.OrdinalIgnoreCase);

        var deleteTl = db.Provider == DatabaseProvider.SqlServer
            ? $"DELETE FROM chronoaudit.TimelineEvents WHERE Id = {eventId}"
            : $"DELETE FROM chronoaudit.\"TimelineEvents\" WHERE \"Id\" = {eventId}";
        var exDeleteTl = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteRawAsync(deleteTl));
        Assert.Contains("append-only", exDeleteTl.Message, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerGuardTriggerTests(SqlServerDatabaseFixture fixture)
    : GuardTriggerTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task DeleteOfAuditRow_IsRefused() => VerifyDeleteOfAuditRow_IsRefusedAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task FirstHashSet_Allowed_SecondHashChange_Refused() => VerifyFirstHashSet_Allowed_SecondHashChange_RefusedAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ChangeOfAction_IsRefused() => VerifyChangeOfAction_IsRefusedAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task BodyRowAndTimelineEvent_CannotBeUpdatedOrDeleted() => VerifyBodyRowAndTimelineEvent_CannotBeUpdatedOrDeletedAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlGuardTriggerTests(PostgreSqlDatabaseFixture fixture)
    : GuardTriggerTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task DeleteOfAuditRow_IsRefused() => VerifyDeleteOfAuditRow_IsRefusedAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task FirstHashSet_Allowed_SecondHashChange_Refused() => VerifyFirstHashSet_Allowed_SecondHashChange_RefusedAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ChangeOfAction_IsRefused() => VerifyChangeOfAction_IsRefusedAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task BodyRowAndTimelineEvent_CannotBeUpdatedOrDeleted() => VerifyBodyRowAndTimelineEvent_CannotBeUpdatedOrDeletedAsync();
}
