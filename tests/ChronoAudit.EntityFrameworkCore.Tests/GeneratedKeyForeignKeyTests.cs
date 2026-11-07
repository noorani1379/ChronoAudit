using System.Text.Json.Nodes;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>A foreign key that points at an entity whose key the database makes must not be audited as EF Core's placeholder either.</summary>
public sealed class GeneratedKeyForeignKeyTests
{
    [Fact]
    public async Task AChildAddedWithItsNewParent_IsAuditedWithTheRealForeignKey()
    {
        await using var shop = await SqliteShop.CreateAsync();
        int ticketId;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var ticket = new Ticket { Title = "parent", Notes = [new TicketNote { Text = "child" }] };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync();
            ticketId = ticket.Id;
        }

        var rows = await shop.AuditRowsAsync();
        Assert.Equal(2, rows.Count);
        var note = JsonNode.Parse(rows.Single(r => r.ResourceType == "TicketNotes").NewValues!)!;
        Assert.Equal(ticketId, note["TicketId"]!.GetValue<int>());
    }

    [Fact]
    public async Task AChildMovedToANewParent_IsAuditedWithTheOldAndTheRealNewForeignKey()
    {
        await using var shop = await SqliteShop.CreateAsync();
        int firstId;
        int secondId;
        Guid noteId;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var first = new Ticket { Title = "first", Notes = [new TicketNote { Text = "moves" }] };
            db.Tickets.Add(first);
            await db.SaveChangesAsync();
            firstId = first.Id;
            noteId = first.Notes[0].Id;
        }

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var note = await db.TicketNotes.SingleAsync(n => n.Id == noteId);
            var second = new Ticket { Title = "second" };
            second.Notes.Add(note);
            db.Tickets.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }

        Assert.NotEqual(firstId, secondId);
        var moved = (await shop.AuditRowsAsync()).Single(r => r.ResourceType == "TicketNotes" && r.Action == AuditActions.AutoModified);
        Assert.Equal(firstId, JsonNode.Parse(moved.OldValues!)!["TicketId"]!.GetValue<int>());
        Assert.Equal(secondId, JsonNode.Parse(moved.NewValues!)!["TicketId"]!.GetValue<int>());
    }
}
