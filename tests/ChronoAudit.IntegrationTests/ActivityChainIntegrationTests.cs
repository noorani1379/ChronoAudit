using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

public abstract class ActivityChainIntegrationTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyParallelWriters_ThenHashing_LeaveOneValidChainAsync()
    {
        var db = Fixture.Database;

        // 64 parallel writers insert API activity rows
        await Parallel.ForEachAsync(Enumerable.Range(0, 64), async (i, ct) =>
        {
            using var scope = db.Scope();
            var logStore = scope.ServiceProvider.GetRequiredService<IApiRequestLogStore>();
            var entry = ApiRequestLogEntry.Create(
                requestId: Guid.NewGuid(),
                occurredAtUtc: DateTimeOffset.UtcNow,
                duration: TimeSpan.FromMilliseconds(42),
                httpMethod: "POST",
                path: $"/api/resource/{i}",
                statusCode: 200,
                userId: $"user-{i}",
                hasBody: i % 2 == 0,
                body: i % 2 == 0 ? $"{{\"item\":{i}}}" : null,
                bodyContentType: i % 2 == 0 ? "application/json" : null);

            await logStore.WriteAsync(entry, ct);
        });

        // Run hasher
        using (var scope = db.Scope())
        {
            var storeDb = db.Store(scope);
            var chain = scope.ServiceProvider.GetRequiredService<StoreChain<ApiRequestLog>>();
            var count = await chain.HashAllPendingAsync(storeDb, batchSize: 10, CancellationToken.None);
            Assert.True(count >= 64, $"Expected at least 64 rows hashed, got {count}");
        }

        // Verify chain via IAuditChainSources
        var sources = db.Services.GetRequiredService<IAuditChainSources>();
        var activitySource = sources.Find("chronoaudit-activity");
        Assert.NotNull(activitySource);

        var verify = await activitySource.VerifyAsync(CancellationToken.None);
        Assert.True(verify.IsValid, $"Activity chain should be valid: {verify.Failure}");
        Assert.True(verify.VerifiedCount >= 64, $"Expected >= 64 verified rows, got {verify.VerifiedCount}");
        Assert.Equal(0, verify.PendingCount);
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerActivityChainIntegrationTests(SqlServerDatabaseFixture fixture)
    : ActivityChainIntegrationTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ParallelWriters_ThenHashing_LeaveOneValidChain() => VerifyParallelWriters_ThenHashing_LeaveOneValidChainAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlActivityChainIntegrationTests(PostgreSqlDatabaseFixture fixture)
    : ActivityChainIntegrationTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ParallelWriters_ThenHashing_LeaveOneValidChain() => VerifyParallelWriters_ThenHashing_LeaveOneValidChainAsync();
}
