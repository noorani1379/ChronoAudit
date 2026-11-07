using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

public abstract class SavedReportConcurrencyTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyAConcurrentChange_IsAConflictAsync()
    {
        var db = Fixture.Database;
        var now = DateTimeOffset.UtcNow;

        Guid reportId;
        using (var scope = db.Scope())
        {
            var store = db.Store(scope);
            var result = SavedReport.Create("user-1", "Original Title", "shop", "{}", null, now);
            Assert.True(result.IsSuccess);
            var report = result.Value;
            store.SavedReports.Add(report);
            await store.SaveChangesAsync();
            reportId = report.Id;
        }

        // Two contexts load the same report concurrently
        using var scope1 = db.Scope();
        using var scope2 = db.Scope();
        var store1 = db.Store(scope1);
        var store2 = db.Store(scope2);

        var report1 = await store1.SavedReports.SingleAsync(r => r.Id == reportId);
        var report2 = await store2.SavedReports.SingleAsync(r => r.Id == reportId);
        var oldStamp = report2.ConcurrencyStamp;

        // First change succeeds
        report1.Change("Changed by 1", "shop", "{}", null, DateTimeOffset.UtcNow);
        await store1.SaveChangesAsync();

        // Second change throws DbUpdateConcurrencyException
        report2.Change("Changed by 2", "shop", "{}", null, DateTimeOffset.UtcNow);
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => store2.SaveChangesAsync());
        Assert.NotNull(ex);

        // And SavedReportService maps stale stamp to SavedReport.Conflict
        var service = scope2.ServiceProvider.GetRequiredService<ISavedReportService>();
        var query = new AnalyticsQuery("audit:shop", ["action"], ["count"], null, now.AddDays(-1), now.AddDays(1));
        var conflictResult = await service.UpdateAsync(
            reportId,
            new SavedReportInput("Changed by 2", query, null),
            oldStamp,
            ChronoAuditScope.OwnOf("user-1"),
            CancellationToken.None);

        Assert.True(conflictResult.IsFailure);
        Assert.Equal("SavedReport.Conflict", conflictResult.Error.Code);
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerSavedReportConcurrencyTests(SqlServerDatabaseFixture fixture)
    : SavedReportConcurrencyTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task AConcurrentChange_IsAConflict() => VerifyAConcurrentChange_IsAConflictAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlSavedReportConcurrencyTests(PostgreSqlDatabaseFixture fixture)
    : SavedReportConcurrencyTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task AConcurrentChange_IsAConflict() => VerifyAConcurrentChange_IsAConflictAsync();
}
