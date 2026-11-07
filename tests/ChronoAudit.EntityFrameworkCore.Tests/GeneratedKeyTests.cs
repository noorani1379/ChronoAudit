using System.Text.Json.Nodes;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>An entity whose key the database makes (identity): its audit row must carry the real key, not EF's placeholder.</summary>
public sealed class GeneratedKeyTests
{
    [Fact]
    public async Task AnInsertedEntity_IsAuditedWithItsRealKey_NotThePlaceholder()
    {
        await using var shop = await SqliteShop.CreateAsync();
        int id;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var ticket = new Ticket { Title = "first" };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            id = ticket.Id;
        }

        Assert.True(id > 0);
        var row = Assert.Single(await shop.AuditRowsAsync());
        Assert.Equal(AuditActions.AutoAdded, row.Action);
        Assert.Equal("Tickets", row.ResourceType);
        Assert.Equal(id, JsonNode.Parse(row.ResourceId)!["Id"]!.GetValue<int>());
        var values = JsonNode.Parse(row.NewValues!)!;
        Assert.Equal(id, values["Id"]!.GetValue<int>());
        Assert.Equal("first", values["Title"]!.GetValue<string>());
        Assert.Equal("user-1", values["CreatedBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task SeveralInsertedEntities_GetOneRowEach_WithTheirOwnKeys()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var ids = new List<int>();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var tickets = new[] { new Ticket { Title = "a" }, new Ticket { Title = "b" }, new Ticket { Title = "c" } };
            db.Tickets.AddRange(tickets);
            var saved = await db.SaveChangesAsync();
            ids.AddRange(tickets.Select(t => t.Id));

            // Three entities and three audit rows.
            Assert.Equal(6, saved);
        }

        var keys = (await shop.AuditRowsAsync()).Select(r => JsonNode.Parse(r.ResourceId)!["Id"]!.GetValue<int>()).OrderBy(k => k).ToList();
        Assert.Equal(ids.OrderBy(k => k), keys);
        Assert.Equal(3, keys.Distinct().Count());
    }

    [Fact]
    public async Task AMixedSave_AuditsEveryChange_AndTheNewEntityKeepsItsRealKey()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var orderId = Guid.NewGuid();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order { Id = orderId, Title = "chair" });
            await db.SaveChangesAsync();
        }

        int ticketId;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var order = await db.Orders.SingleAsync();
            order.Title = "table";
            var ticket = new Ticket { Title = "follow up" };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        var rows = await shop.AuditRowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Action == AuditActions.AutoModified && r.ResourceType == "Orders");
        var added = Assert.Single(rows, r => r.ResourceType == "Tickets");
        Assert.Equal(ticketId, JsonNode.Parse(added.ResourceId)!["Id"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheEntityIsHistoryReadable_ByItsRealKey()
    {
        await using var shop = await SqliteShop.CreateAsync();
        int id;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var ticket = new Ticket { Title = "history" };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            id = ticket.Id;
            ticket.Title = "history (edited)";
            await db.SaveChangesAsync();
        }

        using var read = shop.Scope();
        var page = await read.ServiceProvider.GetRequiredService<IChangeReader>()
            .RecordAsync("shop", "Tickets", id.ToString(System.Globalization.CultureInfo.InvariantCulture), ChronoAuditScope.Everything("admin"), null, 50, false, CancellationToken.None);

        Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Code : null);
        Assert.Equal(new[] { AuditActions.AutoAdded, AuditActions.AutoModified }, page.Value.Items.Select(i => i.Action).ToArray());
    }

    [Fact]
    public async Task WhenTheAuditRowCannotBeWritten_NothingIsStored_AndTheFailureIsNotATransientOne()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            // A trigger that refuses the audit row of a ticket only: the entity itself saves fine, its row does not.
            await SqliteShop.Get<ShopDb>(scope).Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER refuse_ticket_rows BEFORE INSERT ON AuditLogEntries WHEN NEW.ResourceType = 'Tickets' BEGIN SELECT RAISE(ABORT, 'refused'); END");
        }

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Tickets.Add(new Ticket { Title = "doomed" });

            var failure = await Assert.ThrowsAsync<ChronoAuditSaveException>(() => db.SaveChangesAsync());
            Assert.IsType<DbUpdateException>(failure.InnerException);
        }

        using var read = shop.Scope();
        Assert.Empty(await SqliteShop.Get<ShopDb>(read).Tickets.AsNoTracking().ToListAsync());
        Assert.Empty(await shop.AuditRowsAsync());
    }
}
