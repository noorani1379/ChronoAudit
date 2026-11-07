using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

public abstract class ExportClaimTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyTwoWorkersClaimingSameJobConcurrently_ExactlyOneWinsAsync()
    {
        var db = Fixture.Database;
        var now = DateTimeOffset.UtcNow;

        // Seed one pending export job
        Guid jobId;
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var job = ExportJob.Create(
                requestedByUserId: "user-1",
                producer: "analytics",
                format: ExportFormat.Csv,
                parametersJson: "{}",
                scopeKind: ScopeKind.All,
                scopeGroupId: null,
                now: now);

            store.ExportJobs.Add(job);
            await store.SaveChangesAsync();
            jobId = job.Id;
        }

        // Two workers try to claim the job concurrently
        var wins = 0;
        var losses = 0;

        using var scope1 = db.Scope();
        using var scope2 = db.Scope();
        var store1 = db.Store(scope1);
        var store2 = db.Store(scope2);

        var job1 = await store1.ExportJobs.SingleAsync(j => j.Id == jobId);
        var job2 = await store2.ExportJobs.SingleAsync(j => j.Id == jobId);

        var lease = TimeSpan.FromMinutes(5);

        job1.Claim("worker-1", now, lease);
        job2.Claim("worker-2", now, lease);

        var t1 = Task.Run(async () =>
        {
            try
            {
                await store1.SaveChangesAsync();
                Interlocked.Increment(ref wins);
            }
            catch (DbUpdateConcurrencyException)
            {
                Interlocked.Increment(ref losses);
            }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                await store2.SaveChangesAsync();
                Interlocked.Increment(ref wins);
            }
            catch (DbUpdateConcurrencyException)
            {
                Interlocked.Increment(ref losses);
            }
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(1, wins);
        Assert.Equal(1, losses);
    }

    protected async Task VerifyCheckpointSurvives_AndLeaseBehaviorAsync()
    {
        var db = Fixture.Database;
        var now = DateTimeOffset.UtcNow;
        var lease = TimeSpan.FromMinutes(5);

        Guid jobId;
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var job = ExportJob.Create(
                requestedByUserId: "user-1",
                producer: "analytics",
                format: ExportFormat.Csv,
                parametersJson: "{}",
                scopeKind: ScopeKind.All,
                scopeGroupId: null,
                now: now);

            // Claim by worker-1 and write checkpoint
            job.Claim("worker-1", now, lease);
            job.Checkpoint("cursor-100", bytes: 5000, rowsAdded: 100, now, lease);
            store.ExportJobs.Add(job);
            await store.SaveChangesAsync();
            jobId = job.Id;
        }

        // 1. Checkpoint survived in database
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var job = await store.ExportJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal("cursor-100", job.CheckpointCursor);
            Assert.Equal(5000, job.CheckpointBytes);
            Assert.Equal(100, job.RowCount);

            // 2. While lease is live, it is not claimable
            var claimableLive = await store.ExportJobs
                .Where(j => j.Id == jobId && (j.State == ExportState.Pending || (j.State == ExportState.Running && j.LeaseUntilUtc < now)))
                .FirstOrDefaultAsync();
            Assert.Null(claimableLive);
        }

        // Simulate lease expiration
        await db.ExecuteRawAsync(
            db.Provider == DatabaseProvider.SqlServer
                ? $"UPDATE chronoaudit.ExportJobs SET LeaseUntilUtc = DATEADD(minute, -10, GETUTCDATE()) WHERE Id = '{jobId}'"
                : $"UPDATE chronoaudit.\"ExportJobs\" SET \"LeaseUntilUtc\" = now() - interval '10 minutes' WHERE \"Id\" = '{jobId}'");

        // 3. Now expired lease is claimable in fresh context
        using (var scope2 = db.Scope())
        {
            var store2 = db.Store(scope2);
            var claimableExpired = await store2.ExportJobs
                .Where(j => j.Id == jobId && (j.State == ExportState.Pending || (j.State == ExportState.Running && (j.LeaseUntilUtc == null || j.LeaseUntilUtc < DateTimeOffset.UtcNow))))
                .FirstOrDefaultAsync();
            Assert.NotNull(claimableExpired);

            // Next worker claims it and resumes from the checkpoint
            claimableExpired.Claim("worker-2", DateTimeOffset.UtcNow, lease);
            Assert.Equal("cursor-100", claimableExpired.CheckpointCursor);
            Assert.Equal(5000, claimableExpired.CheckpointBytes);
            Assert.Equal(100, claimableExpired.RowCount);

            // Write next batch from checkpoint
            claimableExpired.Checkpoint("cursor-200", bytes: 10000, rowsAdded: 100, DateTimeOffset.UtcNow, lease);
            await store2.SaveChangesAsync();
        }

        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var job = await store.ExportJobs.SingleAsync(j => j.Id == jobId);
            Assert.Equal("cursor-200", job.CheckpointCursor);
            Assert.Equal(10000, job.CheckpointBytes);
            Assert.Equal(200, job.RowCount);
            Assert.Equal("worker-2", job.LeaseOwner);
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerExportClaimTests(SqlServerDatabaseFixture fixture)
    : ExportClaimTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task TwoWorkersClaimingSameJobConcurrently_ExactlyOneWins() => VerifyTwoWorkersClaimingSameJobConcurrently_ExactlyOneWinsAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task CheckpointSurvives_AndLeaseBehavior() => VerifyCheckpointSurvives_AndLeaseBehaviorAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlExportClaimTests(PostgreSqlDatabaseFixture fixture)
    : ExportClaimTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task TwoWorkersClaimingSameJobConcurrently_ExactlyOneWins() => VerifyTwoWorkersClaimingSameJobConcurrently_ExactlyOneWinsAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task CheckpointSurvives_AndLeaseBehavior() => VerifyCheckpointSurvives_AndLeaseBehaviorAsync();
}
