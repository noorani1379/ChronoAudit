using System.Data.Common;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

internal sealed class CommandRecorder : DbCommandInterceptor
{
    private readonly List<(string Text, int Parameters)> _commands = [];

    public IReadOnlyList<(string Text, int Parameters)> Selects
    {
        get
        {
            lock (_commands)
            {
                return _commands.Where(c => c.Text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
    }

    public void Clear()
    {
        lock (_commands)
        {
            _commands.Clear();
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        lock (_commands)
        {
            _commands.Add((command.CommandText, command.Parameters.Count));
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class AnalyticsTranslationTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(ActivityHost Host, CommandRecorder Recorder)> HostAsync()
    {
        var recorder = new CommandRecorder();
        var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics(), storeInterceptor: recorder);
        for (var i = 1; i <= 4; i++)
        {
            await host.LogAsync(i, status: i == 4 ? 500 : 200);
        }

        return (host, recorder);
    }

    [SqliteDecimalFact]
    public async Task ThreeDimensionsAndAllAggregations_TranslateToOneStatement()
    {
        var (host, recorder) = await HostAsync();
        await using var _ = host;
        recorder.Clear();
        var query = new AnalyticsQuery(
            "activity", ["endpoint", "method", "day"], ["count", "distinctUsers", "avgDurationMs", "errors"], null, Day, Day.AddDays(1), SortBy: "count");

        var result = await host.Provider.GetRequiredService<IAnalyticsService>().RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var statement = Assert.Single(recorder.Selects);
        Assert.Contains("GROUP BY", statement.Text, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT", statement.Text, StringComparison.Ordinal);
        Assert.Contains("avg(", statement.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sum(", statement.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", statement.Text, StringComparison.Ordinal);
        Assert.Contains("LIMIT", statement.Text, StringComparison.Ordinal);
    }

    [SqliteDecimalFact]
    public async Task ComparingThePreviousPeriod_IsASecondStatementOfTheSameShape_NotALoop()
    {
        var (host, recorder) = await HostAsync();
        await using var _ = host;
        recorder.Clear();
        var query = new AnalyticsQuery("activity", ["method"], ["count"], null, Day, Day.AddDays(1), ComparePrevious: true);

        Assert.True((await host.Provider.GetRequiredService<IAnalyticsService>().RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None)).IsSuccess);

        Assert.Equal(2, recorder.Selects.Count);
        Assert.All(recorder.Selects, s => Assert.Contains("GROUP BY", s.Text, StringComparison.Ordinal));
    }

    [SqliteDecimalFact]
    public async Task TheDepthThreeTree_TranslatesToOneStatement_WithParameters()
    {
        var (host, recorder) = await HostAsync();
        await using var _ = host;
        recorder.Clear();
        var filters = new[]
        {
            FilterNode.Condition("method", FilterOp.In, "POST", "GET"),
            FilterNode.AnyOf(
                FilterNode.Condition("endpoint", FilterOp.Prefix, "/api/"),
                FilterNode.AllOf(FilterNode.Condition("user", FilterOp.In, "u1", "u2"), FilterNode.Condition("status", FilterOp.Range, "500", string.Empty))),
        };
        var query = new AnalyticsQuery("activity", ["method"], ["count"], filters, Day, Day.AddDays(1));

        var result = await host.Provider.GetRequiredService<IAnalyticsService>().RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var statement = Assert.Single(recorder.Selects);
        Assert.Contains(" OR ", statement.Text, StringComparison.Ordinal);
        Assert.Contains(" AND ", statement.Text, StringComparison.Ordinal);
        Assert.Contains("LIKE", statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("'%", statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", statement.Text, StringComparison.Ordinal);
        Assert.True(statement.Parameters >= 4, "values travel as parameters, not as literals");
    }
}

public sealed class BuiltInSubjectsTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<ActivityHost> HostAsync()
    {
        var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());
        await host.LogAsync(1, endpoint: "/a", ip: "10.0.0.1");
        using var scope = host.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
        db.Orders.Add(new Order { Title = "chair" });
        await db.SaveChangesAsync();
        return host;
    }

    private static string Sample(IReadOnlyList<AnalyticsFilterInfo> filters, string key) => filters.Single(f => f.Key == key).Key;

    [SqliteDecimalFact]
    public async Task TheSubjects_HaveUniqueKeys_AndNonEmptyCatalogs()
    {
        await using var host = await HostAsync();
        var catalog = host.Provider.GetRequiredService<IAnalyticsService>().Catalog(ChronoAuditScope.Everything(null));

        Assert.Equal(new[] { "audit:shop", "activity" }.Order(), catalog.Select(c => c.Key).Order());
        Assert.Equal(catalog.Count, catalog.Select(c => c.Key).Distinct().Count());
        Assert.All(catalog, c =>
        {
            Assert.NotEmpty(c.Dimensions);
            Assert.NotEmpty(c.Measures);
            Assert.NotEmpty(c.Filters);
        });
    }

    [SqliteDecimalFact]
    public async Task EveryDimensionAndMeasure_RunsOnSqlite()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();
        var from = DateTimeOffset.UtcNow.AddYears(-5);
        var to = DateTimeOffset.UtcNow.AddYears(5);

        foreach (var entry in service.Catalog(ChronoAuditScope.Everything(null)))
        {
            foreach (var dimension in entry.Dimensions)
            {
                var query = new AnalyticsQuery(entry.Key, [dimension.Key], entry.Measures.Select(m => m.Key).Take(AnalyticsLimits.MaxMeasures).ToList(), null, Day.AddYears(-1), Day.AddYears(1));
                var result = await service.RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None);
                Assert.True(result.IsSuccess, entry.Key + "/" + dimension.Key + ": " + (result.IsFailure ? result.Error.Message : string.Empty));
            }

            Assert.True(from < to);
        }
    }

    [SqliteDecimalFact]
    public async Task EveryFilterFieldWithEachOfItsOperators_RunsOnSqlite()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();

        foreach (var entry in service.Catalog(ChronoAuditScope.Everything(null)))
        {
            foreach (var field in entry.Filters)
            {
                foreach (var op in field.Operators)
                {
                    var numeric = field.Key == "status";
                    var node = op switch
                    {
                        FilterOp.IsNull => FilterNode.Condition(field.Key, op),
                        FilterOp.Range => FilterNode.Condition(field.Key, op, "200", "599"),
                        FilterOp.In => FilterNode.Condition(field.Key, op, numeric ? "200" : "x", numeric ? "404" : "y"),
                        _ => FilterNode.Condition(field.Key, op, numeric ? "200" : "x"),
                    };
                    var query = new AnalyticsQuery(entry.Key, [entry.Dimensions[0].Key], [entry.Measures[0].Key], [node], Day.AddYears(-1), Day.AddYears(1));
                    var result = await service.RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None);
                    Assert.True(result.IsSuccess, $"{entry.Key}/{field.Key}/{op}: {(result.IsFailure ? result.Error.Message : string.Empty)}");
                }
            }
        }

        Assert.Equal("resourceId", Sample(service.Catalog(ChronoAuditScope.Everything(null)).Single(c => c.Key == "audit:shop").Filters, "resourceId"));
    }

    [SqliteDecimalFact]
    public async Task TheAuditSubject_GroupsByTheStoredCalendarDate()
    {
        await using var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());
        host.Clock.Set(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        for (var i = 0; i < 2; i++)
        {
            using var scope = host.Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        host.Clock.Set(new DateTimeOffset(2026, 12, 5, 10, 0, 0, TimeSpan.Zero));
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        var query = new AnalyticsQuery("audit:shop", ["month"], ["count"], null, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), SortBy: "month", SortDescending: false);
        var result = await host.Provider.GetRequiredService<IAnalyticsService>().RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.Equal(new[] { ("2026/11", (decimal?)2m), ("2026/12", (decimal?)1m) }, result.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0])));
    }
}

public sealed class AnalyticsRegistrationTests
{
    [SqliteDecimalFact]
    public async Task AHostSubject_IsInTheCatalog_AndRuns()
    {
        await using var shop = await SqliteShop.CreateAsync(customize: b => b.AddAnalyticsSubject(
            new AnalyticsSubject<Order>(
                "orders",
                "Orders",
                o => o.CreatedAtUtc,
                [new AnalyticsDimension<Order>("title", "Title", o => o.Title)],
                [AnalyticsMeasure<Order>.Count("count", "Orders"), AnalyticsMeasure<Order>.Of("quantity", "Quantity", Aggregation.Sum, o => (decimal?)o.Quantity)],
                [FilterField.Of<Order, string>("title", "Title", o => o.Title, FilterOp.Eq)]),
            (services, _) => services.GetRequiredService<ShopDb>().Orders));
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.AddRange(new Order { Title = "chair", Quantity = 2 }, new Order { Title = "chair", Quantity = 3 }, new Order { Title = "desk", Quantity = 1 });
            await db.SaveChangesAsync();
        }

        var service = shop.Provider.GetRequiredService<IAnalyticsService>();
        var entry = Assert.Single(service.Catalog(ChronoAuditScope.OwnOf("u1")));
        var now = shop.Clock.GetUtcNow();
        var result = await service.RunAsync(
            new AnalyticsQuery("orders", ["title"], ["count", "quantity"], null, now.AddDays(-1), now.AddDays(1), SortBy: "title", SortDescending: false),
            ChronoAuditScope.OwnOf("u1"),
            CancellationToken.None);

        Assert.Equal("orders", entry.Key);
        Assert.Equal(new[] { ("chair", (decimal?)2m, (decimal?)5m), ("desk", (decimal?)1m, (decimal?)1m) }, result.Value.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0], r.Measures[1])));
    }

    [Fact]
    public void ADuplicateSubjectKey_IsRefusedAtRegistration()
    {
        var services = new ServiceCollection();
        var builder = services.AddChronoAudit(SqliteShop.Key);
        static AnalyticsSubject<Order> Make() => new("orders", "Orders", o => o.CreatedAtUtc, [new AnalyticsDimension<Order>("title", "Title", o => o.Title)], [AnalyticsMeasure<Order>.Count("count", "n")], []);
        builder.AddAnalyticsSubject(Make(), (_, _) => Array.Empty<Order>().AsQueryable());

        Assert.Throws<InvalidOperationException>(() => builder.AddAnalyticsSubject(Make(), (_, _) => Array.Empty<Order>().AsQueryable()));
    }

    [Fact]
    public async Task AnUnknownSubject_IsNotFound()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var now = shop.Clock.GetUtcNow();

        var result = await shop.Provider.GetRequiredService<IAnalyticsService>().RunAsync(
            new AnalyticsQuery("nope", ["a"], ["count"], null, now, now), ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.Equal("Analytics.SubjectNotFound", result.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public async Task ThePlanner_ValidatesWithoutRunning()
    {
        await using var shop = await SqliteShop.CreateAsync(customize: b => b.AddAnalyticsSubject(
            new AnalyticsSubject<Order>("orders", "Orders", o => o.CreatedAtUtc, [new AnalyticsDimension<Order>("title", "Title", o => o.Title)], [AnalyticsMeasure<Order>.Count("count", "n")], []),
            (services, _) => services.GetRequiredService<ShopDb>().Orders));
        var planner = shop.Provider.GetRequiredService<IAnalyticsPlanner>();
        var now = shop.Clock.GetUtcNow();

        Assert.True(planner.Validate(new AnalyticsQuery("orders", ["title"], ["count"], null, now, now), ChronoAuditScope.OwnOf("u1")).IsSuccess);
        Assert.Equal("Filter.NotAllowed", planner.Validate(new AnalyticsQuery("orders", ["Password"], ["count"], null, now, now), ChronoAuditScope.OwnOf("u1")).Error.Code);
        Assert.Equal("Analytics.SubjectNotFound", planner.Validate(new AnalyticsQuery("zzz", ["title"], ["count"], null, now, now), ChronoAuditScope.OwnOf("u1")).Error.Code);
    }
}

public sealed class AnalyticsScopeTests
{
    private static async Task<ActivityHost> HostAsync()
    {
        var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());
        await host.LogAsync(1, userId: "u1");
        await host.LogAsync(2, userId: "u1");
        await host.LogAsync(3, userId: "u2");
        await host.LogAsync(4, userId: null);
        return host;
    }

    private static AnalyticsQuery Count(DateTimeOffset now, string subject = "activity", string dimension = "method")
        => new(subject, [dimension], ["count"], null, now.AddDays(-1), now.AddDays(1));

    [SqliteDecimalFact]
    public async Task Own_SeesOnlyItsRows_AndGroupIsTreatedAsOwn()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();
        var now = host.Clock.GetUtcNow();

        var own = await service.RunAsync(Count(now), ChronoAuditScope.OwnOf("u1"), CancellationToken.None);
        var group = await service.RunAsync(Count(now), ChronoAuditScope.GroupOf("u2", "g1"), CancellationToken.None);

        Assert.Equal(2m, Assert.Single(own.Value.Rows).Measures[0]);
        Assert.Equal(1m, Assert.Single(group.Value.Rows).Measures[0]);
    }

    [SqliteDecimalFact]
    public async Task All_SeesEveryRow_IncludingAnonymousCalls()
    {
        await using var host = await HostAsync();
        var now = host.Clock.GetUtcNow();

        var all = await host.Provider.GetRequiredService<IAnalyticsService>().RunAsync(Count(now), ChronoAuditScope.Everything(null), CancellationToken.None);

        Assert.Equal(4m, Assert.Single(all.Value.Rows).Measures[0]);
    }

    [SqliteDecimalFact]
    public async Task ASubjectThatRequiresAllScope_IsHiddenFromTheCatalog_AndRefusedForOwn()
    {
        await using var host = await HostAsync();
        var service = host.Provider.GetRequiredService<IAnalyticsService>();
        var now = host.Clock.GetUtcNow();

        Assert.Equal(new[] { "activity" }, service.Catalog(ChronoAuditScope.OwnOf("u1")).Select(c => c.Key));
        Assert.Equal(new[] { "activity", "audit:shop" }.Order(), service.Catalog(ChronoAuditScope.Everything(null)).Select(c => c.Key).Order());
        var refused = await service.RunAsync(Count(now, "audit:shop", "action"), ChronoAuditScope.OwnOf("u1"), CancellationToken.None);
        Assert.Equal("Analytics.SubjectForbidden", refused.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Forbidden, refused.Error.Kind);
        Assert.True((await service.RunAsync(Count(now, "audit:shop", "action"), ChronoAuditScope.Everything(null), CancellationToken.None)).IsSuccess);
    }
}

public sealed class ActivityStatisticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<ActivityHost> HostAsync()
    {
        var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());
        host.Clock.Set(T0);
        await host.LogAsync(1, userId: "u1", endpoint: "/a", status: 200);
        await host.LogAsync(2, userId: "u1", endpoint: "/a", status: 404);
        await host.LogAsync(3, userId: "u2", endpoint: "/b", status: 500);
        await host.LogAsync(4, userId: null, endpoint: "/b", status: 200);
        await host.LogAsync(5, userId: null, endpoint: "/b", status: 200);
        host.Clock.Set(T0.AddDays(2));
        await host.LogAsync(6, userId: "u1", endpoint: "/a", status: 200);
        return host;
    }

    private static IActivityStatistics Stats(ActivityHost host) => host.Provider.GetRequiredService<IActivityStatistics>();

    private static readonly ChronoAuditScope All = ChronoAuditScope.Everything(null);

    [SqliteDecimalFact]
    public async Task Counts_AreGroupedByTheAskedDimension()
    {
        await using var host = await HostAsync();
        var stats = Stats(host);

        var byEndpoint = (await stats.CountAsync(T0, T0.AddDays(3), "endpoint", All, CancellationToken.None)).Value;
        var byClass = (await stats.CountAsync(T0, T0.AddDays(3), "statusClass", All, CancellationToken.None)).Value;
        var byDay = (await stats.CountAsync(T0, T0.AddDays(3), "day", All, CancellationToken.None)).Value;
        var byMonth = (await stats.CountAsync(T0, T0.AddDays(3), "month", All, CancellationToken.None)).Value;

        Assert.Equal(new[] { ("/a", 3L), ("/b", 3L) }, byEndpoint.Buckets.Select(b => (b.Key, b.Count)));
        Assert.Equal(new[] { ("2xx", 4L), ("4xx", 1L), ("5xx", 1L) }, byClass.Buckets.Select(b => (b.Key, b.Count)));
        Assert.Equal(new[] { ("2026/11/01", 5L), ("2026/11/03", 1L) }, byDay.Buckets.Select(b => (b.Key, b.Count)));
        Assert.Equal(new[] { ("2026/11", 6L) }, byMonth.Buckets.Select(b => (b.Key, b.Count)));
        Assert.Equal(6, byEndpoint.Total);
        Assert.False(byEndpoint.IsCapped);
    }

    [SqliteDecimalFact]
    public async Task User_GivesEachUserTheirCount_BiggestFirst_WithAnExplicitUnknownBucket()
    {
        await using var host = await HostAsync();

        var result = (await Stats(host).CountAsync(T0, T0.AddDays(3), "user", All, CancellationToken.None)).Value;

        Assert.Equal(new[] { ("u1", 3L), ("(unknown)", 2L), ("u2", 1L) }, result.Buckets.Select(b => (b.Key, b.Count)));
    }

    [SqliteDecimalFact]
    public async Task TheRange_IsInclusiveAtBothEnds_AndNothingOutsideIsCounted()
    {
        await using var host = await HostAsync();
        var stats = Stats(host);

        var exact = (await stats.CountAsync(T0, T0, "endpoint", All, CancellationToken.None)).Value;
        var before = (await stats.CountAsync(T0.AddDays(-5), T0.AddTicks(-1), "endpoint", All, CancellationToken.None)).Value;
        var laterOnly = (await stats.CountAsync(T0.AddDays(1), T0.AddDays(2), "endpoint", All, CancellationToken.None)).Value;

        Assert.Equal(5, exact.Total);
        Assert.Equal(0, before.Total);
        Assert.Equal(1, laterOnly.Total);
    }

    [SqliteDecimalFact]
    public async Task AnEmptyRange_GivesNoBuckets_AndAZeroTotal()
    {
        await using var host = await HostAsync();

        var result = (await Stats(host).CountAsync(T0.AddYears(-3), T0.AddYears(-3).AddDays(1), "day", All, CancellationToken.None)).Value;

        Assert.Empty(result.Buckets);
        Assert.Equal(0, result.Total);
        Assert.False(result.IsCapped);
    }

    [SqliteDecimalFact]
    public async Task TheBuckets_AreCapped_AndTheResultSaysSo_WhileTheTotalStaysWhole()
    {
        await using var host = await ActivityHost.CreateAsync(customize: b => b.AddBuiltInAnalytics());
        host.Clock.Set(T0);
        const int endpoints = AnalyticsLimits.MaxTopN + 3;
        for (var i = 1; i <= endpoints; i++)
        {
            await host.LogAsync(i, endpoint: "/e/" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        }

        var result = (await Stats(host).CountAsync(T0, T0.AddDays(1), "endpoint", All, CancellationToken.None)).Value;

        Assert.Equal(AnalyticsLimits.MaxTopN, result.Buckets.Count);
        Assert.True(result.IsCapped);
        Assert.Equal(endpoints, result.Total);
    }

    [SqliteDecimalFact]
    public async Task TheRange_IsAtMost366Days_AndTheDimensionMustBeKnown()
    {
        await using var host = await HostAsync();
        var stats = Stats(host);

        var wide = await stats.CountAsync(T0, T0.AddDays(367), "day", All, CancellationToken.None);
        var reversed = await stats.CountAsync(T0, T0.AddDays(-1), "day", All, CancellationToken.None);
        var unknown = await stats.CountAsync(T0, T0.AddDays(1), "ip", All, CancellationToken.None);

        Assert.Equal("Analytics.RangeTooWide", wide.Error.Code);
        Assert.Equal("ChronoAudit.Validation", reversed.Error.Code);
        Assert.Equal("Filter.NotAllowed", unknown.Error.Code);
        Assert.True((await stats.CountAsync(T0, T0.AddDays(366), "day", All, CancellationToken.None)).IsSuccess);
    }

    [SqliteDecimalFact]
    public async Task PerUserFigures_NeedTheAllScope_AndOwnSeesOnlyItsCalls()
    {
        await using var host = await HostAsync();
        var stats = Stats(host);

        var denied = await stats.CountAsync(T0, T0.AddDays(3), "user", ChronoAuditScope.OwnOf("u1"), CancellationToken.None);
        var own = (await stats.CountAsync(T0, T0.AddDays(3), "endpoint", ChronoAuditScope.OwnOf("u2"), CancellationToken.None)).Value;

        Assert.Equal("Analytics.SubjectForbidden", denied.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Forbidden, denied.Error.Kind);
        Assert.Equal(new[] { ("/b", 1L) }, own.Buckets.Select(b => (b.Key, b.Count)));
    }
}

/// <summary>A fact that needs SQLite to aggregate and order decimals: EF Core 8's SQLite provider cannot, EF Core 10's can. SQL Server and PostgreSQL are proven in the integration tests (P12).</summary>
internal sealed class SqliteDecimalFactAttribute : FactAttribute
{
    public SqliteDecimalFactAttribute()
    {
#if !NET10_0_OR_GREATER
        Skip = "EF Core 8 SQLite cannot aggregate or order decimals; covered on net10.0 and by the real-database tests.";
#endif
    }
}
