using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

public abstract class AnalyticsIntegrationTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyAuditAnalytics_DimensionsMeasuresAndFiltersAsync()
    {
        var db = Fixture.Database;
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        // Seed 3 orders with different titles and actions
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            shop.Orders.Add(new Order { Title = "Alpha", Quantity = 10 });
            shop.Orders.Add(new Order { Title = "Beta", Quantity = 20 });
            shop.Orders.Add(new Order { Title = "Gamma", Quantity = 30 });
            await shop.SaveChangesAsync();
        }

        using var queryScope = db.Scope();
        var analytics = queryScope.ServiceProvider.GetRequiredService<IAnalyticsService>();

        // 1. Group by action and resourceType
        var q1 = new AnalyticsQuery(
            "audit:shop",
            ["action", "resourceType"],
            ["count"],
            null,
            now.AddDays(-2),
            now.AddDays(2));

        var r1 = await analytics.RunAsync(q1, ChronoAuditScope.Everything(null), CancellationToken.None);
        Assert.True(r1.IsSuccess);
        var rows1 = r1.Value.Rows;
        Assert.NotEmpty(rows1);
        Assert.True(rows1.Sum(b => b.Measures[0] ?? 0) >= 3);

        // 2. Test filter operators on action and resourceType
        var q2 = new AnalyticsQuery(
            "audit:shop",
            ["action"],
            ["count"],
            [
                FilterNode.Condition("action", FilterOp.Eq, AuditActions.AutoAdded),
                FilterNode.Condition("resourceType", FilterOp.In, "Orders", "Payments"),
            ],
            now.AddDays(-2),
            now.AddDays(2));

        var r2 = await analytics.RunAsync(q2, ChronoAuditScope.Everything(null), CancellationToken.None);
        Assert.True(r2.IsSuccess);
        Assert.NotEmpty(r2.Value.Rows);
    }

    protected async Task VerifyActivityAnalytics_DimensionsAndMeasuresAsync()
    {
        var db = Fixture.Database;
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        // Seed API requests
        using (var scope = db.Scope())
        {
            var logStore = scope.ServiceProvider.GetRequiredService<IApiRequestLogStore>();
            await logStore.WriteAsync(ApiRequestLogEntry.Create(
                Guid.NewGuid(), now, TimeSpan.FromMilliseconds(100), "GET", "/api/items", 200, "user-1"), default);
            await logStore.WriteAsync(ApiRequestLogEntry.Create(
                Guid.NewGuid(), now, TimeSpan.FromMilliseconds(200), "GET", "/api/items", 200, "user-2"), default);
            await logStore.WriteAsync(ApiRequestLogEntry.Create(
                Guid.NewGuid(), now, TimeSpan.FromMilliseconds(500), "POST", "/api/items", 201, "user-1"), default);
        }

        using var queryScope = db.Scope();
        var analytics = queryScope.ServiceProvider.GetRequiredService<IAnalyticsService>();

        // Group by method and statusClass, with count, avgDurationMs, maxDurationMs
        var q = new AnalyticsQuery(
            "activity",
            ["method", "statusClass"],
            ["count", "avgDurationMs", "maxDurationMs"],
            [FilterNode.Condition("method", FilterOp.Eq, "GET")],
            now.AddDays(-1),
            now.AddDays(1));

        var result = await analytics.RunAsync(q, ChronoAuditScope.Everything(null), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Value.Rows);

        var getRow = result.Value.Rows.FirstOrDefault(r => r.Dimensions[0] == "GET");
        Assert.NotNull(getRow);
        Assert.Equal(2, getRow.Measures[0]);
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerAnalyticsIntegrationTests(SqlServerDatabaseFixture fixture)
    : AnalyticsIntegrationTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task AuditAnalytics_DimensionsMeasuresAndFilters() => VerifyAuditAnalytics_DimensionsMeasuresAndFiltersAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ActivityAnalytics_DimensionsAndMeasures() => VerifyActivityAnalytics_DimensionsAndMeasuresAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlAnalyticsIntegrationTests(PostgreSqlDatabaseFixture fixture)
    : AnalyticsIntegrationTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task AuditAnalytics_DimensionsMeasuresAndFilters() => VerifyAuditAnalytics_DimensionsMeasuresAndFiltersAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ActivityAnalytics_DimensionsAndMeasures() => VerifyActivityAnalytics_DimensionsAndMeasuresAsync();
}
