using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class ResilientStoreTests
{
    private sealed class FlakyStore(int failures) : IApiRequestLogStore
    {
        public int Calls { get; private set; }

        public int Stored { get; private set; }

        public Task WriteAsync(ApiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls <= failures)
            {
                throw new InvalidOperationException("database down");
            }

            Stored++;
            return Task.CompletedTask;
        }
    }

    private static ApiRequestLogEntry Entry(string? userId = "u1")
        => ApiRequestLogEntry.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(3), "POST", "/api/x", 200, userId, body: "{\"password\":\"***MASKED***\"}", bodyContentType: "application/json");

    private static (ResilientApiRequestLogStore Store, CapturingLoggerProvider Logs, List<TimeSpan> Pauses, FakeClock Clock) Build(IApiRequestLogStore inner)
    {
        var logs = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        var pauses = new List<TimeSpan>();
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        return (new ResilientApiRequestLogStore(inner, factory, clock, pause =>
        {
            pauses.Add(pause);
            return Task.CompletedTask;
        }), logs, pauses, clock);
    }

    [Fact]
    public async Task ASuccessfulWrite_NeedsOneTry_NoPause_NoLog()
    {
        var inner = new FlakyStore(0);
        var (store, logs, pauses, _) = Build(inner);

        await store.WriteAsync(Entry(), CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Empty(pauses);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task AWriteThatFailsTwice_IsRetriedWithJitteredPauses_AndSucceeds()
    {
        var inner = new FlakyStore(2);
        var (store, logs, pauses, _) = Build(inner);

        await store.WriteAsync(Entry(), CancellationToken.None);

        Assert.Equal(3, inner.Calls);
        Assert.Equal(1, inner.Stored);
        Assert.Equal(2, pauses.Count);
        Assert.InRange(pauses[0].TotalMilliseconds, 75, 125);
        Assert.InRange(pauses[1].TotalMilliseconds, 300, 500);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task WhenEveryTryFails_TheEntryGoesToTheFallbackLog_AndNothingIsThrown()
    {
        var inner = new FlakyStore(int.MaxValue);
        var (store, logs, _, _) = Build(inner);
        var entry = Entry();

        await store.WriteAsync(entry, CancellationToken.None);

        Assert.Equal(ResilientApiRequestLogStore.MaxTries, inner.Calls);
        var line = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Critical, line.Level);
        Assert.Equal("ChronoAudit.ActivityLog.Fallback", line.Category);
        Assert.Contains("API_ACTIVITY_LOG_FALLBACK", line.Message, StringComparison.Ordinal);
        Assert.Contains(entry.RequestId.ToString(), line.Message, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAFailure_TheBreakerKeepsTheDatabaseUntouched_ThenTriesAgain()
    {
        var inner = new FlakyStore(3);
        var (store, logs, _, clock) = Build(inner);

        await store.WriteAsync(Entry(), CancellationToken.None);
        Assert.Equal(3, inner.Calls);

        // The breaker is open: no database call, an Error (not another Critical) in the fallback log.
        await store.WriteAsync(Entry(), CancellationToken.None);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(new[] { LogLevel.Critical, LogLevel.Error }, logs.Entries.Select(e => e.Level));
        Assert.Contains("circuit_open", logs.Entries[1].Message, StringComparison.Ordinal);

        clock.Advance(ResilientApiRequestLogStore.BreakerOpenFor + TimeSpan.FromSeconds(1));
        await store.WriteAsync(Entry(), CancellationToken.None);

        Assert.Equal(4, inner.Calls);
        Assert.Equal(1, inner.Stored);
        Assert.Equal(2, logs.Entries.Count);
    }

    [Fact]
    public async Task ANewAlert_IsRaised_AfterASuccess()
    {
        var inner = new FlakyStore(3);
        var (store, logs, _, clock) = Build(inner);
        await store.WriteAsync(Entry(), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.WriteAsync(Entry(), CancellationToken.None);

        Assert.Equal(1, inner.Stored);
        Assert.Single(logs.Entries);
    }

    [Fact]
    public void TheEntryFactory_CutsEveryTextToItsColumn()
    {
        var entry = ApiRequestLogEntry.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromDays(100), new string('M', 50), new string('p', 900), 99999,
            userId: new string('u', 100), sessionId: new string('s', 100), endpointKey: new string('e', 400), queryString: new string('q', 3000),
            errorCode: new string('c', 300), ipAddress: new string('i', 100), userAgent: new string('a', 900), bodyContentType: new string('c', 500));

        Assert.Equal(ApiRequestLogEntry.MaxMethodLength, entry.HttpMethod.Length);
        Assert.Equal(ApiRequestLogEntry.MaxPathLength, entry.Path.Length);
        Assert.Equal(ApiRequestLogEntry.MaxIdLength, entry.UserId!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxIdLength, entry.SessionId!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxEndpointKeyLength, entry.EndpointKey!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxQueryStringLength, entry.QueryString!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxErrorCodeLength, entry.ErrorCode!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxIpAddressLength, entry.IpAddress!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxUserAgentLength, entry.UserAgent!.Length);
        Assert.Equal(ApiRequestLogEntry.MaxContentTypeLength, entry.BodyContentType!.Length);
        Assert.Equal(short.MaxValue, entry.StatusCode);
        Assert.Equal(int.MaxValue, entry.DurationMs);
        Assert.Equal("?", ApiRequestLogEntry.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.Zero, " ", null, 200).HttpMethod);
    }
}

public sealed class ActivityStoreTests
{
    [Fact]
    public async Task ARow_IsStoredWithItsBody_AndAUniqueRequestId_IsIdempotent()
    {
        await using var host = await ActivityHost.CreateAsync();

        await host.LogAsync(1, body: "{\"a\":1}");
        await host.LogAsync(1, body: "{\"a\":1}");

        var rows = await host.ReadStoreAsync(db => db.Set<ApiRequestLog>().Include(x => x.Body).AsNoTracking().ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal("{\"a\":1}", row.Body!.Body);
        Assert.Equal("application/json", row.Body.ContentType);
        Assert.Equal(TimeSpan.Zero, row.OccurredAtUtc.Offset);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error && e.Category.StartsWith("ChronoAudit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARowWithoutABody_HasNoBodyRow()
    {
        await using var host = await ActivityHost.CreateAsync();

        await host.LogAsync(1);

        Assert.Equal(0, await host.ReadStoreAsync(db => db.Set<ApiRequestBody>().CountAsync()));
    }

    [Fact]
    public async Task CalendarParts_FollowTheConfiguredCalendar()
    {
        await using var gregorian = await ActivityHost.CreateAsync();
        gregorian.Clock.Set(new DateTimeOffset(2026, 3, 21, 8, 0, 0, TimeSpan.Zero));
        await gregorian.LogAsync(1);

        await using var persian = await ActivityHost.CreateAsync(o =>
        {
            o.Calendar.Kind = "Persian";
            o.Calendar.TimeZoneId = "UTC";
        });
        persian.Clock.Set(new DateTimeOffset(2026, 3, 21, 8, 0, 0, TimeSpan.Zero));
        await persian.LogAsync(1);

        var g = await gregorian.ReadStoreAsync(db => db.Set<ApiRequestLog>().AsNoTracking().SingleAsync());
        var p = await persian.ReadStoreAsync(db => db.Set<ApiRequestLog>().AsNoTracking().SingleAsync());
        Assert.Equal((2026, 3, 21, 1), ((int)g.CalendarYear!, (int)g.CalendarMonth!, (int)g.CalendarDay!, (int)g.CalendarQuarter!));
        Assert.Equal((1405, 1, 1, 1), ((int)p.CalendarYear!, (int)p.CalendarMonth!, (int)p.CalendarDay!, (int)p.CalendarQuarter!));
    }

    [Theory]
    [InlineData("Julian", "UTC")]
    [InlineData("Gregorian", "Nowhere/Land")]
    public void AnUnknownCalendarOrZone_StopsTheStart(string kind, string zone)
    {
        var options = new CalendarOptions { Kind = kind, TimeZoneId = zone };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Create());

        Assert.Contains("ChronoAudit:Calendar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIntentWrite_ThatFails_ReachesTheCaller()
    {
        await using var host = await ActivityHost.CreateAsync();
        var store = host.Provider.GetRequiredService<IApiMutationIntentStore>();
        await host.IntentAsync(1);

        // The same request id again is idempotent...
        await host.IntentAsync(1);
        Assert.Equal(1, await host.ReadStoreAsync(db => db.Set<ApiMutationIntent>().CountAsync()));

        // ...but a real failure (a missing required value) is thrown, so the filter can refuse the call.
        await Assert.ThrowsAnyAsync<Exception>(() => store.WriteAsync(
            new ApiMutationIntent { RequestId = Guid.NewGuid(), StartedAtUtc = DateTimeOffset.UtcNow, Method = null!, Path = "/x", BodySha256 = "a" }, CancellationToken.None));
    }

    [Fact]
    public void UseStore_TwiceWithAnotherContext_IsRefused()
    {
        var services = new ServiceCollection();
        var builder = services.AddChronoAudit(SqliteShop.Key).UseStore<StoreDb>();

        builder.UseStore<StoreDb>();
        Assert.Throws<InvalidOperationException>(() => builder.UseStore<ShopDb>());
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddChronoAudit(SqliteShop.Key).AddActivityLog());
    }

    [Fact]
    public void TheStoreSchemaName_IsValidated()
    {
        Assert.Throws<InvalidOperationException>(() => new ChronoAuditStoreOptions { Schema = "bad schema" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ChronoAuditStoreOptions { Schema = string.Empty }.Validate());
        new ChronoAuditStoreOptions { Schema = "audit_2" }.Validate();
    }
}

public sealed class StoreModelTests
{
    [Fact]
    public void TheStoreTables_HaveTheSchemaAndIndexesOfThePlan()
    {
        // The InMemory provider is not SQLite, so the model keeps the schema and the double-quote filters.
        var options = new DbContextOptionsBuilder<StoreDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new StoreDb(options);
        var model = db.Model;

        var log = model.FindEntityType(typeof(ApiRequestLog))!;
        var body = model.FindEntityType(typeof(ApiRequestBody))!;
        var intent = model.FindEntityType(typeof(ApiMutationIntent))!;

        Assert.Equal(("ApiRequestLogs", "chronoaudit"), (log.GetTableName(), log.GetSchema()));
        Assert.Equal(("ApiRequestBodies", "chronoaudit"), (body.GetTableName(), body.GetSchema()));
        Assert.Equal(("ApiMutationIntents", "chronoaudit"), (intent.GetTableName(), intent.GetSchema()));

        Assert.Equal(64, log.FindProperty(nameof(ApiRequestLog.UserId))!.GetMaxLength());
        Assert.Equal(64, log.FindProperty(nameof(ApiRequestLog.SessionId))!.GetMaxLength());
        Assert.Equal(8, log.FindProperty(nameof(ApiRequestLog.HttpMethod))!.GetMaxLength());
        Assert.Equal(512, log.FindProperty(nameof(ApiRequestLog.Path))!.GetMaxLength());
        Assert.Equal(256, log.FindProperty(nameof(ApiRequestLog.EndpointKey))!.GetMaxLength());
        Assert.Equal(2048, log.FindProperty(nameof(ApiRequestLog.QueryString))!.GetMaxLength());
        Assert.Equal(64, log.FindProperty(nameof(ApiRequestLog.PayloadHash))!.GetMaxLength());
        Assert.Equal(typeof(short?), log.FindProperty(nameof(ApiRequestLog.CalendarYear))!.ClrType);
        Assert.Equal(64, intent.FindProperty(nameof(ApiMutationIntent.BodySha256))!.GetMaxLength());

        var logIndexes = log.GetIndexes().ToDictionary(i => i.GetDatabaseName()!);
        Assert.True(logIndexes["UX_ApiRequestLogs_RequestId"].IsUnique);
        Assert.True(logIndexes["UX_ApiRequestLogs_ChainIndex"].IsUnique);
        Assert.Equal("\"ChainIndex\" IS NOT NULL", logIndexes["UX_ApiRequestLogs_ChainIndex"].GetFilter());
        Assert.Equal("\"PayloadHash\" IS NULL", logIndexes["IX_ApiRequestLogs_Unhashed"].GetFilter());
        Assert.Equal("\"CorrelationId\" IS NOT NULL", logIndexes["IX_ApiRequestLogs_Correlation"].GetFilter());
        Assert.Contains("IX_ApiRequestLogs_User", logIndexes.Keys);
        Assert.Contains("IX_ApiRequestLogs_Endpoint", logIndexes.Keys);
        Assert.Contains("IX_ApiRequestLogs_Session", logIndexes.Keys);

        var intentIndexes = intent.GetIndexes().ToDictionary(i => i.GetDatabaseName()!);
        Assert.True(intentIndexes["UX_ApiMutationIntents_RequestId"].IsUnique);
        Assert.True(intentIndexes["UX_ApiMutationIntents_ChainIndex"].IsUnique);
        Assert.Equal("\"PayloadHash\" IS NULL", intentIndexes["IX_ApiMutationIntents_Unhashed"].GetFilter());

        var foreignKey = Assert.Single(body.GetForeignKeys());
        Assert.Equal(DeleteBehavior.NoAction, foreignKey.DeleteBehavior);
        Assert.Equal(nameof(ApiRequestBody.ApiRequestLogId), Assert.Single(body.FindPrimaryKey()!.Properties).Name);

        Assert.Contains(log.GetDeclaredTriggers(), t => t.ModelName == "TR_ApiRequestLogs_Guard");
        Assert.Contains(body.GetDeclaredTriggers(), t => t.ModelName == "TR_ApiRequestBodies_Guard");
        Assert.Contains(intent.GetDeclaredTriggers(), t => t.ModelName == "TR_ApiMutationIntents_Guard");
    }

    [Fact]
    public async Task OnSqlite_TheSchemaIsIgnored_AndTheTablesAreCreated()
    {
        await using var host = await ActivityHost.CreateAsync();

        var entity = await host.ReadStoreAsync(db => Task.FromResult(db.Model.FindEntityType(typeof(ApiRequestLog))!.GetSchema()));

        Assert.Null(entity);
        Assert.Equal(0, await host.ReadStoreAsync(db => db.Set<ApiMutationIntent>().CountAsync()));
    }
}
