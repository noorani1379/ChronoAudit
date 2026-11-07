using System.Text.Json.Nodes;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class HistoryTests
{
    [Fact]
    public async Task Tables_AreDiscovered_AndExcludedOnesLeftOut()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var source = shop.Provider.GetRequiredService<IModuleAuditHistorySource>();

        Assert.Equal("shop", source.Module);
        Assert.Equal(new[] { "CustomerProfiles", "OrderLines", "Orders", "Payments", "Receipts", "TicketNotes", "Tickets" }, source.Tables.Select(t => t.Table));

        var orders = source.Tables.Single(t => t.Table == "Orders");
        Assert.True(orders.SoftDelete);
        Assert.False(orders.AppendOnly);
        Assert.True(orders.RecordLookup);
        Assert.Equal(new AuditedKeyPart("Id", "guid"), Assert.Single(orders.Key));
        Assert.True(orders.Columns.Single(c => c.Name == "Secret").Secret);
        Assert.False(orders.Columns.Single(c => c.Name == "Title").Secret);

        Assert.True(source.Tables.Single(t => t.Table == "Receipts").AppendOnly);
        Assert.Equal(new[] { "OrderId", "LineNo" }, source.Tables.Single(t => t.Table == "OrderLines").Key.Select(k => k.Name));
        Assert.DoesNotContain(source.Tables, t => t.Table is "AuditLogEntries" or "Noises");

        var history = shop.Provider.GetRequiredService<IAuditHistory>();
        Assert.Equal(new[] { "shop" }, history.Modules);
        Assert.Same(source, history.Find("SHOP"));
        Assert.Null(history.Find("nope"));
    }

    [Fact]
    public async Task ARecordsHistory_IsOldestFirst_AndPaged()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        await shop.SeedAuditRowsAsync(
            (1, "Orders", AuditResourceKey.Of(mine), "Auto_Added"),
            (2, "Orders", AuditResourceKey.Of(other), "Auto_Added"),
            (3, "Orders", AuditResourceKey.Of(mine), "Auto_Modified"),
            (5, "Orders", AuditResourceKey.Of(mine), "Auto_SoftDeleted"));
        var source = shop.Provider.GetRequiredService<IModuleAuditHistorySource>();

        var all = await source.ListRecordChangesAsync("Orders", mine.ToString(), null, 10, CancellationToken.None);
        Assert.Equal(new long[] { 1, 3, 5 }, all.Select(r => r.Sequence));

        var next = await source.ListRecordChangesAsync("Orders", mine.ToString(), afterSequence: 1, limit: 1, CancellationToken.None);
        Assert.Equal(new long[] { 3 }, next.Select(r => r.Sequence));

        var byId = await source.ListChangesAsync("Orders", mine, null, 10, CancellationToken.None);
        Assert.Equal(new long[] { 1, 3, 5 }, byId.Select(r => r.Sequence));
    }

    [Fact]
    public async Task ACompositeKey_IsFoundByItsParts()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var orderId = Guid.NewGuid();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.OrderLines.Add(new OrderLine { OrderId = orderId, LineNo = 2, Sku = "A" });
            db.OrderLines.Add(new OrderLine { OrderId = orderId, LineNo = 3, Sku = "B" });
            await db.SaveChangesAsync();
        }

        var source = shop.Provider.GetRequiredService<IModuleAuditHistorySource>();
        var rows = await source.ListRecordChangesAsync("OrderLines", $"{orderId}|2", null, 10, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("A", JsonNode.Parse(row.NewValuesJson!)!["Sku"]!.GetValue<string>());
        Assert.Empty(await source.ListRecordChangesAsync("OrderLines", $"{orderId}", null, 10, CancellationToken.None));
        Assert.Empty(await source.ListRecordChangesAsync("OrderLines", $"{orderId}|x", null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownTableBadKeyOrExcluded_GivesNothing()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await shop.SeedAuditRowsAsync((1, "Orders", AuditResourceKey.Of(Guid.NewGuid()), "Auto_Added"));
        var source = shop.Provider.GetRequiredService<IModuleAuditHistorySource>();

        Assert.Empty(await source.ListRecordChangesAsync("Nope", Guid.NewGuid().ToString(), null, 10, CancellationToken.None));
        Assert.Empty(await source.ListRecordChangesAsync("Orders", "not-a-guid", null, 10, CancellationToken.None));
        Assert.Empty(await source.ListRecordChangesAsync("Noises", Guid.NewGuid().ToString(), null, 10, CancellationToken.None));
        Assert.Empty(await source.ListTableChangesAsync("Nope", null, null, 0, 10, CancellationToken.None));
    }

    [Fact]
    public async Task APage_IsClampedToMaxPlusOne()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var id = Guid.NewGuid();
        await shop.SeedAuditRowsAsync(Enumerable.Range(1, IModuleAuditHistorySource.MaxPageSize + 5)
            .Select(i => ((long)i, "Orders", AuditResourceKey.Of(id), "Auto_Modified")).ToArray());
        var source = shop.Provider.GetRequiredService<IModuleAuditHistorySource>();

        var page = await source.ListTableChangesAsync("Orders", null, null, 0, int.MaxValue, CancellationToken.None);

        Assert.Equal(IModuleAuditHistorySource.MaxPageSize + 1, page.Count);
        Assert.Equal(page.Select(r => r.Row.Sequence).Order(), page.Select(r => r.Row.Sequence));
        var record = await source.ListRecordChangesAsync("Orders", id.ToString(), null, int.MaxValue, CancellationToken.None);
        Assert.Equal(IModuleAuditHistorySource.MaxPageSize + 1, record.Count);
    }

    [Fact]
    public async Task ATableHistory_RespectsTheTimeRange_AndTheModuleListFiltersByTables()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var id = Guid.NewGuid();
        await shop.SeedAuditRowsAsync(
            (1, "Orders", AuditResourceKey.Of(id), "Auto_Added"),
            (2, "Receipts", AuditResourceKey.Of(id), "Auto_Added"),
            (3, "Orders", AuditResourceKey.Of(id), "Auto_Modified"));
        var source = shop.Provider.GetRequiredService<IModuleAuditHistorySource>();
        var t0 = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

        var ranged = await source.ListTableChangesAsync("Orders", t0.AddSeconds(2), t0.AddSeconds(3), 0, 10, CancellationToken.None);
        Assert.Equal(new long[] { 3 }, ranged.Select(r => r.Row.Sequence));

        var module = await source.ListModuleChangesAsync(t0, t0.AddHours(1), 0, 10, ["Receipts"], CancellationToken.None);
        Assert.Equal(new long[] { 2 }, module.Select(r => r.Row.Sequence));
        var whole = await source.ListModuleChangesAsync(t0, t0.AddHours(1), 1, 10, CancellationToken.None);
        Assert.Equal(new long[] { 2, 3 }, whole.Select(r => r.Row.Sequence));
    }
}
