using System.Net;
using System.Text;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using ChronoAudit.EntityFrameworkCore.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore.Tests;

internal static class Seed
{
    public static readonly string Range = "from=2026-10-20T00:00:00Z&to=2026-11-15T00:00:00Z";

    // Saves orders (one audit row each) and returns the audit table name and the ids.
    public static async Task<(string Table, List<Guid> Ids)> OrdersAsync(HttpRig rig, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            using var scope = rig.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            var order = new Order { Title = "order-" + i, Secret = "hidden" };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            ids.Add(order.Id);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        return ((await rig.AuditRowsAsync())[0].ResourceType, ids);
    }

    public static async Task HashAsync(HttpRig rig)
    {
        using var scope = rig.Services.CreateScope();
        await rig.Services.GetRequiredService<AuditChain>().HashAllPendingAsync(scope.ServiceProvider.GetRequiredService<ShopDb>(), 500, CancellationToken.None, "shop");
    }
}

public sealed class HistoryEndpointTests
{
    [Fact]
    public async Task TheCatalog_ListsTheModulesAndTheirAuditedTables()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.GetAsync("/chronoaudit/history/tables", Who.Me());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        var shop = body.RootElement.EnumerateArray().Single(m => m.GetProperty("module").GetString() == "shop");
        Assert.Contains(shop.GetProperty("tables").EnumerateArray(), t => t.GetProperty("entity").GetString() == "Order");
    }

    [Fact]
    public async Task TheTableHistory_IsPaged_AndNeedsTheAllScope()
    {
        await using var rig = await HttpRig.StartAsync();
        var (table, _) = await Seed.OrdersAsync(rig, 3);

        var first = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes?limit=2", Who.Admin);
        using var firstBody = await HttpRig.JsonAsync(first);
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();
        var second = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes?limit=2&cursor={Uri.EscapeDataString(cursor!)}", Who.Admin);
        using var secondBody = await HttpRig.JsonAsync(second);
        var own = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes", Who.Me());

        Assert.Equal(2, firstBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, secondBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(firstBody.RootElement.GetProperty("personalDataRevealed").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task TheRecordHistory_IsFoundByItsKey()
    {
        await using var rig = await HttpRig.StartAsync();
        var (table, ids) = await Seed.OrdersAsync(rig, 2);

        var response = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/records/{ids[1]}/changes", Who.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Contains(ids[1].ToString("D"), item.GetProperty("recordKey").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BadInput_GetsAnExactStatusAndCode()
    {
        await using var rig = await HttpRig.StartAsync();
        var (table, _) = await Seed.OrdersAsync(rig, 1);

        var limit = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes?limit=abc", Who.Admin);
        var zero = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes?limit=0", Who.Admin);
        var reveal = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes?reveal=maybe", Who.Admin);
        var module = await rig.GetAsync($"/chronoaudit/history/nope/tables/{table}/changes", Who.Admin);
        var typo = await rig.GetAsync($"/chronoaudit/history/shop/tables/{table}/changes?limt=5", Who.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, limit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reveal.StatusCode);
        Assert.True(module.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        using var body = await HttpRig.JsonAsync(typo);
        Assert.Equal("Filter.NotAllowed", body.RootElement.GetProperty("title").GetString());
        Assert.Contains("limt", body.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }
}

public sealed class AuditEndpointTests
{
    private sealed class NoAuditRecorder : IAuditRecorder
    {
        public Task<ChronoAuditResult> RecordAsync(string actorUserId, string action, string resourceType, string resourceId, object? metadata, CancellationToken cancellationToken)
            => Task.FromResult(ChronoAuditResult.Failure(ChronoAuditError.Unavailable("ChronoAudit.AuditUnavailable", "down")));
    }

    [Fact]
    public async Task TheSearch_ReturnsTheRows_ForTheAllScopeOnly()
    {
        await using var rig = await HttpRig.StartAsync();
        await Seed.OrdersAsync(rig, 3);

        var admin = await rig.GetAsync($"/chronoaudit/audit/shop/search?{Seed.Range}&limit=2", Who.Admin);
        var own = await rig.GetAsync($"/chronoaudit/audit/shop/search?{Seed.Range}", Who.Me());
        var noRange = await rig.GetAsync("/chronoaudit/audit/shop/search", Who.Admin);

        using var body = await HttpRig.JsonAsync(admin);
        Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
        Assert.NotNull(body.RootElement.GetProperty("nextCursor").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noRange.StatusCode);
        using var problem = await HttpRig.JsonAsync(noRange);
        Assert.Equal("Analytics.RangeRequired", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheVerification_IsRecordedBeforeItRuns_AndReportsTheChain()
    {
        await using var rig = await HttpRig.StartAsync();
        await Seed.OrdersAsync(rig, 3);
        await Seed.HashAsync(rig);

        var response = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "shop" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        Assert.True(body.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(3, body.RootElement.GetProperty("verifiedCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("pendingCount").GetInt32()); // the run own audit row, recorded first, is not hashed yet
        Assert.Contains(await rig.AuditRowsAsync(), a => a.Action == AuditActions.ChainVerified && a.ActorUserId == "admin");
    }

    [Fact]
    public async Task ATamperedRow_IsReportedByTheVerification()
    {
        await using var rig = await HttpRig.StartAsync();
        await Seed.OrdersAsync(rig, 3);
        await Seed.HashAsync(rig);
        using (var scope = rig.Services.CreateScope())
        {
#pragma warning disable EF1002
            await scope.ServiceProvider.GetRequiredService<ShopDb>().Database.ExecuteSqlRawAsync("UPDATE AuditLogEntries SET Action = 'forged' WHERE SequenceNumber = 2");
#pragma warning restore EF1002
        }

        var response = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "shop" });

        using var body = await HttpRig.JsonAsync(response);
        Assert.False(body.RootElement.GetProperty("valid").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("failedAtSequence").ValueKind);
    }

    [Fact]
    public async Task AResumedRun_ReportsWhereToContinue()
    {
        await using var rig = await HttpRig.StartAsync();
        await Seed.OrdersAsync(rig, 4);
        await Seed.HashAsync(rig);

        var first = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "shop", maxRows = 2 });
        using var firstBody = await HttpRig.JsonAsync(first);
        var next = firstBody.RootElement.GetProperty("nextAfterChainIndex").GetInt64();
        var second = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "shop", afterChainIndex = next });
        using var secondBody = await HttpRig.JsonAsync(second);

        Assert.Equal(2, firstBody.RootElement.GetProperty("verifiedCount").GetInt32());
        Assert.True(secondBody.RootElement.GetProperty("valid").GetBoolean());
        Assert.True(secondBody.RootElement.GetProperty("verifiedCount").GetInt32() >= 2);
    }

    [Fact]
    public async Task BadVerifyCalls_AreRefused_AndNothingIsRecorded()
    {
        await using var rig = await HttpRig.StartAsync();

        var unknown = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "nope" });
        var negative = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "shop", afterChainIndex = -1 });
        var blank = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = " " });
        var own = await rig.PostAsync("/chronoaudit/audit/verify", Who.Me(), new { module = "shop" });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        using var problem = await HttpRig.JsonAsync(unknown);
        Assert.Equal("Chain.UnknownModule", problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(await rig.AuditRowsAsync(), a => a.Action == AuditActions.ChainVerified);
    }

    [Fact]
    public async Task WhenTheRunCannotBeRecorded_ItDoesNotRun()
    {
        await using var rig = await HttpRig.StartAsync(services: s => s.AddSingleton<IAuditRecorder>(new NoAuditRecorder()));

        var response = await rig.PostAsync("/chronoaudit/audit/verify", Who.Admin, new { module = "shop" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = await HttpRig.JsonAsync(response);
        Assert.Equal("ChronoAudit.AuditUnavailable", problem.RootElement.GetProperty("title").GetString());
    }
}

public sealed class ActivityEndpointTests
{
    private static async Task LogCallsAsync(HttpRig rig)
    {
        await rig.GetAsync("/api/hello", Who.Me("u1"));
        await rig.GetAsync("/api/hello", Who.Me("u1"));
        await rig.GetAsync("/api/hello", Who.Me("u2"));
        await rig.GetAsync("/api/teapot", Who.Me("u2"));
    }

    private static string Window() => $"from={HttpRig.T0.AddDays(-1):O}&to={HttpRig.T0.AddDays(1):O}".Replace("+", "%2B", StringComparison.Ordinal);

    [Fact]
    public async Task TheSearch_ShowsAnOwnScopeUserOnlyTheirCalls_AndAnAdminEverything()
    {
        await using var rig = await HttpRig.StartAsync();
        await LogCallsAsync(rig);

        var own = await rig.GetAsync($"/chronoaudit/activity/search?{Window()}", Who.Me("u1"));
        var admin = await rig.GetAsync($"/chronoaudit/activity/search?{Window()}", Who.Admin);
        var other = await rig.GetAsync($"/chronoaudit/activity/search?{Window()}&userId=u2", Who.Me("u1"));
        var noRange = await rig.GetAsync("/chronoaudit/activity/search", Who.Admin);

        using var ownBody = await HttpRig.JsonAsync(own);
        using var adminBody = await HttpRig.JsonAsync(admin);
        Assert.All(ownBody.RootElement.GetProperty("items").EnumerateArray(), i => Assert.Equal("u1", i.GetProperty("userId").GetString()));
        Assert.True(adminBody.RootElement.GetProperty("items").GetArrayLength() >= 4);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noRange.StatusCode);
    }

    [SqliteDecimalFact]
    public async Task TheStatistics_CountByTheAskedDimension_AndTheUserDimensionNeedsTheAllScope()
    {
        await using var rig = await HttpRig.StartAsync();
        await LogCallsAsync(rig);

        var byClass = await rig.GetAsync($"/chronoaudit/activity/statistics?{Window()}&by=statusClass", Who.Admin);
        var users = await rig.GetAsync($"/chronoaudit/activity/statistics?{Window()}&by=user", Who.Me());
        var unknown = await rig.GetAsync($"/chronoaudit/activity/statistics?{Window()}&by=ip", Who.Admin);

        Assert.Equal(HttpStatusCode.OK, byClass.StatusCode);
        using var body = await HttpRig.JsonAsync(byClass);
        Assert.Equal(4, body.RootElement.GetProperty("total").GetInt64() - 0);
        Assert.Contains(body.RootElement.GetProperty("buckets").EnumerateArray(), b => b.GetProperty("key").GetString() == "2xx");
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task TheBody_OfAnUnknownCall_Is404_AndOfARealOne_IsAuditedThenReturned()
    {
        await using var rig = await HttpRig.StartAsync();
        await rig.PostAsync("/api/echo", Who.Me("u1"), rawJson: """{"title":"chair"}""");
        var id = (await rig.LogsAsync()).Single().Id;

        var unknown = await rig.GetAsync("/chronoaudit/activity/requests/999999/body", Who.Admin);
        var own = await rig.GetAsync($"/chronoaudit/activity/requests/{id}/body", Who.Me());
        var real = await rig.GetAsync($"/chronoaudit/activity/requests/{id}/body", Who.Admin);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        using var body = await HttpRig.JsonAsync(real);
        Assert.Contains("chair", body.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains(await rig.AuditRowsAsync(), a => a.Action == AuditActions.ActivityBodyViewed);
    }

    [Fact]
    public async Task IncompleteIntents_AreListedForTheAllScopeOnly()
    {
        await using var rig = await HttpRig.StartAsync();
        var old = HttpRig.T0.AddMinutes(-30);
        await rig.Services.GetRequiredService<IApiMutationIntentStore>().WriteAsync(
            new ApiMutationIntent { RequestId = Guid.NewGuid(), StartedAtUtc = old, UserId = "u1", Method = "POST", Path = "/api/x", BodySha256 = new string('a', 64) }, CancellationToken.None);

        var admin = await rig.GetAsync("/chronoaudit/activity/intents/incomplete", Who.Admin);
        var own = await rig.GetAsync("/chronoaudit/activity/intents/incomplete", Who.Me());

        using var body = await HttpRig.JsonAsync(admin);
        Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
    }

    [Fact]
    public async Task TheCsvExport_IsBoundedAndAudited()
    {
        await using var rig = await HttpRig.StartAsync();
        await LogCallsAsync(rig);

        var noRange = await rig.GetAsync("/chronoaudit/activity/export.csv", Who.Admin);
        var response = await rig.GetAsync($"/chronoaudit/activity/export.csv?{Window()}", Who.Admin);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var typo = await rig.GetAsync($"/chronoaudit/activity/export.csv?{Window()}&limit=5", Who.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, noRange.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, typo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Id,RequestId,OccurredAtUtc", lines[0], StringComparison.Ordinal);
        Assert.True(lines.Length >= 5);
        Assert.Contains(await rig.AuditRowsAsync(), a => a.Action == AuditActions.ActivityExported);
    }

    [Fact]
    public async Task TheCsvExport_OverTheCap_IsRefusedBeforeAnyByte_AndNothingIsAudited()
    {
        await using var rig = await HttpRig.StartAsync();
        using (var scope = rig.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDb>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var chunk = 0; chunk < 11; chunk++)
            {
                for (var i = 0; i < 10_000; i++)
                {
                    db.Set<ApiRequestLog>().Add(new ApiRequestLog
                    {
                        RequestId = Guid.NewGuid(),
                        OccurredAtUtc = HttpRig.T0,
                        HttpMethod = "GET",
                        Path = "/bulk",
                        StatusCode = 200,
                    });
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }

        var response = await rig.GetAsync($"/chronoaudit/activity/export.csv?{Window()}", Who.Admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = await HttpRig.JsonAsync(response);
        Assert.Equal("Export.TooLarge", problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(await rig.AuditRowsAsync(), a => a.Action == AuditActions.ActivityExported);
    }
}
