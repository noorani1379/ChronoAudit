using System.Diagnostics;
using System.Text.Json;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace ChronoAudit.IntegrationTests;

public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CHRONOAUDIT_SCALE") != "1")
        {
            Skip = "Set CHRONOAUDIT_SCALE=1 to run the 1,000,000 scale measurement.";
        }
    }
}

[Trait("Category", "Integration")]
public sealed class ScaleMeasurement(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static string? GetVar(string name)
        => Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    [ScaleFact]
    public async Task Measure_SqlServer_Scale()
    {
        if (string.IsNullOrWhiteSpace(GetVar("CHRONOAUDIT_TEST_SQLSERVER")))
        {
            output.WriteLine("Skipping SQL Server scale measurement: CHRONOAUDIT_TEST_SQLSERVER not set.");
            return;
        }

        await RunScaleMeasurementAsync(DatabaseProvider.SqlServer);
    }

    [ScaleFact]
    public async Task Measure_PostgreSql_Scale()
    {
        if (string.IsNullOrWhiteSpace(GetVar("CHRONOAUDIT_TEST_POSTGRES")))
        {
            output.WriteLine("Skipping PostgreSQL scale measurement: CHRONOAUDIT_TEST_POSTGRES not set.");
            return;
        }

        await RunScaleMeasurementAsync(DatabaseProvider.PostgreSql);
    }

    private async Task RunScaleMeasurementAsync(DatabaseProvider provider)
    {
        output.WriteLine($"=== Starting Scale Measurement on {provider} (1,000,000 rows) ===");
        var swTotal = Stopwatch.StartNew();

        await using var db = await TestDatabase.CreateAsync(provider, now: Now);

        // 1. Bulk insert 1,000,000 rows with raw SQL
        output.WriteLine("Populating 1,000,000 rows...");
        var swInsert = Stopwatch.StartNew();

        using (var scope = db.Scope())
        {
            var storeDb = db.Store(scope);
            if (provider == DatabaseProvider.SqlServer)
            {
                var sql = """
                    WITH N(n) AS (SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1),
                    N2(n) AS (SELECT 1 FROM N a CROSS JOIN N b),
                    N4(n) AS (SELECT 1 FROM N2 a CROSS JOIN N2 b),
                    N6(n) AS (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM N4 a CROSS JOIN N2 b)
                    INSERT INTO chronoaudit.ApiRequestLogs (RequestId, OccurredAtUtc, DurationMs, UserId, HttpMethod, Path, EndpointKey, StatusCode, CalendarYear, CalendarMonth, CalendarDay, CalendarQuarter, HasBody, PreviousHash, PayloadHash, ChainIndex)
                    SELECT
                        NEWID(),
                        DATEADD(second, - (n % 2592000), '2026-11-01T12:00:00Z'),
                        (n % 500) + 10,
                        'user-' + CAST((n % 100) AS VARCHAR(10)),
                        CASE n % 4 WHEN 0 THEN 'GET' WHEN 1 THEN 'POST' WHEN 2 THEN 'PUT' ELSE 'DELETE' END,
                        '/api/items/' + CAST((n % 1000) AS VARCHAR(10)),
                        '/api/items/item',
                        CASE WHEN n % 20 = 0 THEN 500 ELSE 200 END,
                        2026,
                        10 + ((n % 60) / 30),
                        1 + (n % 28),
                        4,
                        0,
                        NULL,
                        CASE WHEN n <= 5000 THEN NULL ELSE 'archived' END,
                        NULL
                    FROM N6
                    WHERE n <= 1000000;
                    """;
                await storeDb.Database.ExecuteSqlRawAsync(sql);
            }
            else
            {
                var sql = """
                    INSERT INTO chronoaudit."ApiRequestLogs" ("RequestId", "OccurredAtUtc", "DurationMs", "UserId", "HttpMethod", "Path", "EndpointKey", "StatusCode", "CalendarYear", "CalendarMonth", "CalendarDay", "CalendarQuarter", "HasBody", "PreviousHash", "PayloadHash", "ChainIndex")
                    SELECT
                        gen_random_uuid(),
                        '2026-11-01 12:00:00+00'::timestamptz - (s % 2592000 || ' seconds')::interval,
                        (s % 500) + 10,
                        'user-' || (s % 100),
                        CASE s % 4 WHEN 0 THEN 'GET' WHEN 1 THEN 'POST' WHEN 2 THEN 'PUT' ELSE 'DELETE' END,
                        '/api/items/' || (s % 1000),
                        '/api/items/item',
                        CASE WHEN s % 20 = 0 THEN 500 ELSE 200 END,
                        2026,
                        10 + ((s % 60) / 30),
                        1 + (s % 28),
                        4,
                        false,
                        NULL,
                        CASE WHEN s <= 5000 THEN NULL ELSE 'archived' END,
                        NULL
                    FROM generate_series(1, 1000000) s;
                    """;
                await storeDb.Database.ExecuteSqlRawAsync(sql);
            }
        }
        swInsert.Stop();

        // Statistics after a bulk load: what autovacuum (PostgreSQL) and auto-update statistics (SQL Server) do on a running system within minutes.
        await db.ExecuteRawAsync(provider == DatabaseProvider.SqlServer ? "UPDATE STATISTICS chronoaudit.ApiRequestLogs" : "ANALYZE chronoaudit.\"ApiRequestLogs\"");
        output.WriteLine($"Populated 1,000,000 rows in {swInsert.ElapsedMilliseconds:N0} ms.");

        // 2. Measure hashing the backlog (5,000 unhashed rows)
        int hashedCount;
        long hashTimeMs;
        using (var scope = db.Scope())
        {
            var storeDb = db.Store(scope);
            var chain = scope.ServiceProvider.GetRequiredService<StoreChain<ApiRequestLog>>();
            var swHash = Stopwatch.StartNew();
            hashedCount = await chain.HashAllPendingAsync(storeDb, batchSize: 5000, CancellationToken.None);
            swHash.Stop();
            hashTimeMs = swHash.ElapsedMilliseconds;
            output.WriteLine($"Hashed backlog: {hashedCount} rows in {hashTimeMs:N0} ms ({swHash.Elapsed.TotalSeconds:F2} s).");
        }

        // 3. Measure one analytics query (2 dimensions, 2 measures, 30 days)
        long analyticsTimeMs;
        int analyticsRowCount;
        using (var scope = db.Scope())
        {
            var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
            var q = new AnalyticsQuery(
                "activity",
                ["method", "statusClass"],
                ["count", "avgDurationMs"],
                null,
                Now.AddDays(-30),
                Now);

            // Warm-up
            await analytics.RunAsync(q, ChronoAuditScope.Everything(null), CancellationToken.None);

            var swAnalytics = Stopwatch.StartNew();
            var rAnalytics = await analytics.RunAsync(q, ChronoAuditScope.Everything(null), CancellationToken.None);
            swAnalytics.Stop();
            analyticsTimeMs = swAnalytics.ElapsedMilliseconds;
            Assert.True(rAnalytics.IsSuccess);
            analyticsRowCount = rAnalytics.Value.Rows.Count;
            output.WriteLine($"Analytics query: {analyticsRowCount} groups returned in {analyticsTimeMs:N0} ms.");
        }

        // 4. Measure one activity search page (limit 50)
        long searchTimeMs;
        int searchCount;
        using (var scope = db.Scope())
        {
            var search = scope.ServiceProvider.GetRequiredService<IActivitySearch>();
            var query = new ActivitySearchQuery(Now.AddDays(-30), Now, Limit: 50);

            // Warm-up
            await search.SearchAsync(query, CancellationToken.None);

            var swSearch = Stopwatch.StartNew();
            var rSearch = await search.SearchAsync(query, CancellationToken.None);
            swSearch.Stop();
            searchTimeMs = swSearch.ElapsedMilliseconds;
            Assert.True(rSearch.IsSuccess);
            searchCount = rSearch.Value.Items.Count;
            output.WriteLine($"Activity search page: {searchCount} items in {searchTimeMs:N0} ms.");
        }

        // 5. Measure one export of 100,000 rows
        long exportTimeMs;
        var exportedTotal = 0;
        using (var scope = db.Scope())
        {
            var producers = scope.ServiceProvider.GetServices<IExportProducer>();
            var activityProducer = producers.Single(p => p.Key == "activity");
            var query = new ActivitySearchQuery(Now.AddDays(-30), Now, Limit: 200);
            var exportParams = JsonSerializer.SerializeToElement(query);

            var swExport = Stopwatch.StartNew();
            string? cursor = null;
            while (exportedTotal < 100_000)
            {
                var batch = await activityProducer.NextAsync(exportParams, ChronoAuditScope.Everything(null), cursor, 200, CancellationToken.None);
                Assert.True(batch.IsSuccess);
                exportedTotal += batch.Value.Rows.Count;
                cursor = batch.Value.NextCursor;
                if (batch.Value.Done || cursor is null)
                {
                    break;
                }
            }
            swExport.Stop();
            exportTimeMs = swExport.ElapsedMilliseconds;
            output.WriteLine($"Exported {exportedTotal:N0} rows in {exportTimeMs:N0} ms ({swExport.Elapsed.TotalSeconds:F2} s).");
        }

        swTotal.Stop();
        output.WriteLine($"=== Scale Measurement Summary ({provider}) ===");
        output.WriteLine($"| Operation | Target | Measured | Result |");
        output.WriteLine($"|---|---|---|---|");
        output.WriteLine($"| 1M Data Load | 1,000,000 rows | {swInsert.ElapsedMilliseconds:N0} ms | Success |");
        output.WriteLine($"| Backlog Hashing | {hashedCount:N0} rows | {hashTimeMs:N0} ms | Success |");
        output.WriteLine($"| Analytics Query | 30d, 2 dim, 2 meas | {analyticsTimeMs:N0} ms | {analyticsRowCount} rows |");
        output.WriteLine($"| Activity Search Page | 50 rows | {searchTimeMs:N0} ms | {searchCount} items |");
        output.WriteLine($"| Export | 100,000 rows | {exportTimeMs:N0} ms | {exportedTotal:N0} rows |");
        output.WriteLine($"Total test time: {swTotal.Elapsed.TotalSeconds:F1} s.");
    }
}
