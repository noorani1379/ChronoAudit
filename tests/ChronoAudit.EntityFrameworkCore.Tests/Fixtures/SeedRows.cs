using ChronoAudit.EntityFrameworkCore;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public static class SqliteShopSeedExtensions
{
    /// <summary>Adds audit rows with a given sequence (SQLite accepts an explicit value for an integer key); each is one second after 2026-10-31T08:00Z per sequence.</summary>
    public static async Task SeedAuditRowsAsync(this SqliteShop shop, params (long Sequence, string Table, string Key, string Action)[] rows)
    {
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        db.BypassAuditingForSeed = true;
        foreach (var (sequence, table, key, action) in rows)
        {
            var entry = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = "user-1",
                Action = action,
                ResourceType = table,
                ResourceId = key,
                NewValues = "{\"Title\":\"row " + sequence + "\"}",
            };
            entry.StampTime(new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence), 20261031);
            db.AuditLogEntries.Add(entry);
            db.Entry(entry).Property(nameof(AuditLogEntry.SequenceNumber)).CurrentValue = sequence;
        }

        await db.SaveChangesAsync();
    }
}
