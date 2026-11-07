using System.Text.Json.Nodes;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>What an adopter gets by deriving from <see cref="AuditableDbContext"/>: the save pipeline, end to end on a relational provider.</summary>
public sealed class CaptureTests
{
    [Fact]
    public async Task Insert_WritesOneAutoAddedRow_WithActorCorrelationIpSession_AndStampsTheEntity()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order { Title = "chair", Quantity = 2 });
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await shop.AuditRowsAsync());
        Assert.Equal(AuditActions.AutoAdded, row.Action);
        Assert.Equal("Orders", row.ResourceType);
        Assert.Equal("user-1", row.ActorUserId);
        Assert.Equal(shop.Correlation.CorrelationId, row.CorrelationId);
        Assert.Null(row.OldValues);
        Assert.Equal("chair", JsonNode.Parse(row.NewValues!)!["Title"]!.GetValue<string>());
        Assert.Equal(new[] { "Ip", "SessionId" }, JsonNode.Parse(row.Metadata!)!.AsObject().Select(p => p.Key));
        Assert.Null(row.PayloadHash);
        Assert.Null(row.ChainIndex);

        using var read = shop.Scope();
        var order = await SqliteShop.Get<ShopDb>(read).Orders.AsNoTracking().SingleAsync();
        Assert.Equal("user-1", order.CreatedBy);
        Assert.Equal("Ada", order.CreatedByName);
        Assert.Equal(shop.Clock.GetUtcNow(), order.CreatedAtUtc);
        Assert.False(order.IsDeleted);
    }

    [Fact]
    public async Task SensitiveProperty_IsMasked_InOldAndNewValues()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var order = new Order { Secret = "s3cr3t" };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            order.Secret = "other";
            await db.SaveChangesAsync();
        }

        var rows = await shop.AuditRowsAsync();
        Assert.Equal(2, rows.Count);
        foreach (var value in rows.SelectMany(r => new[] { r.OldValues, r.NewValues }).Where(v => v is not null))
        {
            Assert.DoesNotContain("s3cr3t", value, StringComparison.Ordinal);
            Assert.DoesNotContain("other", value, StringComparison.Ordinal);
        }

        Assert.Equal("***MASKED***", JsonNode.Parse(rows[1].NewValues!)!["Secret"]!.GetValue<string>());
        Assert.Equal("***MASKED***", JsonNode.Parse(rows[1].OldValues!)!["Secret"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_WritesOnlyTheChangedColumns_AndNothingWhenNothingChanged()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        var order = new Order { Title = "a", Quantity = 1 };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        shop.Clock.Advance(TimeSpan.FromMinutes(5));
        order.Quantity = 5;
        await db.SaveChangesAsync();
        await db.SaveChangesAsync(); // nothing changed

        var rows = await shop.AuditRowsAsync();
        Assert.Equal(2, rows.Count);
        var modified = rows[^1];
        Assert.Equal(AuditActions.AutoModified, modified.Action);
        Assert.Equal(1, JsonNode.Parse(modified.OldValues!)!["Quantity"]!.GetValue<int>());
        Assert.Equal(5, JsonNode.Parse(modified.NewValues!)!["Quantity"]!.GetValue<int>());
        Assert.DoesNotContain("Title", JsonNode.Parse(modified.NewValues!)!.AsObject().Select(p => p.Key));
        Assert.Equal("user-1", order.ModifiedBy);
        Assert.Equal(shop.Clock.GetUtcNow(), order.ModifiedAtUtc);
        Assert.Equal(rows[0].OccurredAtUtc.AddMinutes(5), modified.OccurredAtUtc);
    }

    [Fact]
    public async Task Delete_OfAnAuditableEntity_IsASoftDelete_HiddenByTheFilter_AndAudited()
    {
        await using var shop = await SqliteShop.CreateAsync();
        Guid id;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var order = new Order { Title = "gone" };
            id = order.Id;
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            db.Orders.Remove(order);
            await db.SaveChangesAsync();
        }

        using var read = shop.Scope();
        var readDb = SqliteShop.Get<ShopDb>(read);
        Assert.Equal(0, await readDb.Orders.CountAsync());
        var kept = await readDb.Orders.IgnoreQueryFilters().SingleAsync(o => o.Id == id);
        Assert.True(kept.IsDeleted);
        Assert.Equal("user-1", kept.DeletedBy);
        Assert.NotNull(kept.DeletedAtUtc);
        Assert.Equal(AuditActions.AutoSoftDeleted, (await shop.AuditRowsAsync())[^1].Action);
    }

    [Fact]
    public async Task Delete_OfAnEntityWithoutSoftDelete_IsPhysical_AndTheAuditRowKeepsItsLastState()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var orderId = Guid.NewGuid();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var line = new OrderLine { OrderId = orderId, LineNo = 1, Sku = "X-1" };
            db.OrderLines.Add(line);
            await db.SaveChangesAsync();
            db.OrderLines.Remove(line);
            await db.SaveChangesAsync();
        }

        using var read = shop.Scope();
        Assert.Equal(0, await SqliteShop.Get<ShopDb>(read).OrderLines.CountAsync());
        var rows = await shop.AuditRowsAsync();
        var last = rows[^1];
        Assert.Equal(AuditActions.AutoDeleted, last.Action);
        Assert.Equal("X-1", JsonNode.Parse(last.OldValues!)!["Sku"]!.GetValue<string>());
        Assert.Null(last.NewValues);

        // The composite key is written in key order.
        var key = JsonNode.Parse(rows[0].ResourceId)!.AsObject();
        Assert.Equal(new[] { "OrderId", "LineNo" }, key.Select(p => p.Key));
    }

    [Fact]
    public async Task AnExcludedEntity_WritesNoAuditRow()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Noises.Add(new Noise { Number = 1 });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await shop.AuditRowsAsync());
        using var read = shop.Scope();
        Assert.Equal(1, await SqliteShop.Get<ShopDb>(read).Noises.CountAsync());
    }

    [Fact]
    public async Task AppendOnlyRows_CanBeInserted_ButNeverEditedOrDeleted()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        var receipt = new Receipt { Text = "paid" };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();

        receipt.Text = "edited";
        var edit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", edit.Message, StringComparison.Ordinal);

        db.ChangeTracker.Clear();
        db.Receipts.Attach(receipt);
        db.Receipts.Remove(receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        using var read = shop.Scope();
        Assert.Equal("paid", (await SqliteShop.Get<ShopDb>(read).Receipts.AsNoTracking().SingleAsync()).Text);
    }

    [Fact]
    public async Task TheAuditTable_CannotBeEditedOrDeleted_ExceptItsHashColumnsOnce()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await shop.SeedAuditRowAsync("Orders", "{\"Id\":1}", AuditActions.AutoAdded);

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.AuditLogEntries.Remove(await db.AuditLogEntries.SingleAsync());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("cannot be deleted", ex.Message, StringComparison.Ordinal);
        }

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var row = await db.AuditLogEntries.SingleAsync();
            db.Entry(row).Property(nameof(AuditLogEntry.Action)).CurrentValue = "x";
            db.Entry(row).Property(nameof(AuditLogEntry.Action)).IsModified = true;
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("only its hash columns", ex.Message, StringComparison.Ordinal);
        }

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var row = await db.AuditLogEntries.SingleAsync();
            row.SetHash(1, null, "ABCDEF");
            await db.SaveChangesAsync();

            // A second hash is refused by the entity itself.
            Assert.Throws<InvalidOperationException>(() => row.SetHash(2, "ABCDEF", "123456"));
        }

        var stored = Assert.Single(await shop.AuditRowsAsync());
        Assert.Equal("ABCDEF", stored.PayloadHash);
        Assert.Equal(1, stored.ChainIndex);
        Assert.Null(stored.PreviousHash);
        Assert.Equal(AuditActions.AutoAdded, stored.Action);
    }

    [Fact]
    public async Task TheSynchronousSave_IsRefused()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        db.Orders.Add(new Order());

        var ex = Assert.Throws<NotSupportedException>(() => db.SaveChanges());

        Assert.Contains("SaveChangesAsync", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await shop.AuditRowsAsync());
    }

    [Fact]
    public async Task WithoutAUser_TheSystemActorIsRecorded()
    {
        await using var shop = await SqliteShop.CreateAsync();
        shop.Actor.UserId = null;
        shop.Actor.DisplayName = null;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        Assert.Equal(AuditTrailOptions.Default.SystemUserId, (await shop.AuditRowsAsync()).Single().ActorUserId);
        using var read = shop.Scope();
        var order = await SqliteShop.Get<ShopDb>(read).Orders.AsNoTracking().SingleAsync();
        Assert.Equal(AuditTrailOptions.Default.SystemDisplayName, order.CreatedByName);
        Assert.Equal("System", order.CreatedBy);
    }

    [Fact]
    public async Task ADelegatedAction_FormatsTheName_AndPutsHostMetadataFirst()
    {
        await using var shop = await SqliteShop.CreateAsync();
        shop.Actor.IsDelegated = true;
        shop.Actor.ActorUserId = "agent-9";
        shop.Actor.AuditMetadata = new { OnBehalfOf = "owner-1" };
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        var row = (await shop.AuditRowsAsync()).Single();
        Assert.Equal("agent-9", row.ActorUserId);
        Assert.Equal("{\"OnBehalfOf\":\"owner-1\",\"Ip\":\"10.0.0.7\",\"SessionId\":\"sess-1\"}", row.Metadata);
        using var read = shop.Scope();
        Assert.Equal("Ada (on behalf)", (await SqliteShop.Get<ShopDb>(read).Orders.AsNoTracking().SingleAsync()).CreatedByName);
    }

    [Fact]
    public async Task ACustomDelegatedFormat_FromTheOptions_IsUsed()
    {
        await using var shop = await SqliteShop.CreateAsync(o => o.Audit.DelegatedDisplayNameFormat = "proxy of {0}");
        shop.Actor.IsDelegated = true;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        using var read = shop.Scope();
        Assert.Equal("proxy of Ada", (await SqliteShop.Get<ShopDb>(read).Orders.AsNoTracking().SingleAsync()).CreatedByName);
    }

    [Fact]
    public async Task AHandWrittenRow_IsSavedWithTheNextSave()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            var writer = SqliteShop.Get<IAuditLogWriter>(scope);
            await writer.AppendHashedAsync("user-1", "ReportViewed", "Reports", "7", new { Note = "q3" });
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
            Assert.Empty(SqliteShop.Get<IAuditLogAccessor>(scope).GetPendingLogs());
        }

        var rows = await shop.AuditRowsAsync();
        var manual = rows.Single(r => r.Action == "ReportViewed");
        Assert.Equal("{\"Note\":\"q3\",\"Ip\":\"10.0.0.7\",\"SessionId\":\"sess-1\"}", manual.Metadata);
        Assert.Equal(2, rows.Count);
        Assert.Equal(shop.Clock.GetUtcNow(), manual.OccurredAtUtc);
        Assert.Equal(20261101, manual.CalendarDate);
    }

    [Fact]
    public async Task AHandWrittenRow_IsNotSaved_WhenTheBusinessSaveFails()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        await SqliteShop.Get<IAuditLogWriter>(scope).AppendHashedAsync("user-1", "ReportViewed", "Reports", "7");
        var receipt = new Receipt();
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        await SqliteShop.Get<IAuditLogWriter>(scope).AppendHashedAsync("user-1", "SecondRow", "Reports", "8");
        receipt.Text = "edited";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        Assert.DoesNotContain(await shop.AuditRowsAsync(), r => r.Action == "SecondRow");
        Assert.Single(SqliteShop.Get<IAuditLogAccessor>(scope).GetPendingLogs());
    }

    [Fact]
    public async Task TheSeedBypass_SavesWithoutAuditRows()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.BypassAuditingForSeed = true;
            db.Orders.Add(new Order { Title = "seed" });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await shop.AuditRowsAsync());
        using var read = shop.Scope();
        Assert.Equal(1, await SqliteShop.Get<ShopDb>(read).Orders.CountAsync());
    }

    [Fact]
    public async Task TheHostHooks_RunInOrder_AndAfterSavedOnlyOnSuccess()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        db.Orders.Add(new Order());
        await db.SaveChangesAsync();

        Assert.Equal(new[] { "before-save", "before-audit", "after-saved" }, db.Calls);

        db.Calls.Clear();
        db.Receipts.Add(new Receipt());
        await db.SaveChangesAsync();
        db.Receipts.Local.Single().Text = "edited";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal(new[] { "before-save", "before-audit", "after-saved" }, db.Calls);
    }

    [Fact]
    public async Task ActorDefaults_AreUsed_WhenTheHostDoesNotOverrideThem()
    {
        IAuditActor minimal = new MinimalAuditActor();
        Assert.Null(minimal.UserIp);
        Assert.Null(minimal.SessionId);
        Assert.Null(minimal.Role);
        Assert.Null(minimal.RoleTitle);
        Assert.False(minimal.IsDelegated);
        Assert.Null(minimal.AuditMetadata);
        Assert.Equal(minimal.UserId, minimal.ActorUserId);

        var options = new DbContextOptionsBuilder<ShopDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var accessor = new ScopedAuditLogManager(new FakeCorrelation(Guid.NewGuid()), minimal);
        await using var db = new ShopDb(options, minimal, new FakeCorrelation(Guid.NewGuid()), accessor, new AuditSignalChannel(), AuditTrailOptions.Default);
        db.Orders.Add(new Order());
        await db.SaveChangesAsync();

        var row = await db.AuditLogEntries.AsNoTracking().SingleAsync();
        Assert.Equal("min-user", row.ActorUserId);
        Assert.Null(row.Metadata);
    }

    [Theory]
    [InlineData("cashier", "teller", "cashier")]
    [InlineData(null, "teller", "teller")]
    [InlineData("", "", "Ada")]
    public async Task ActorNameAsRole_IsStored_WhenTheEntityIsMarked(string? roleTitle, string? role, string expected)
    {
        await using var shop = await SqliteShop.CreateAsync();
        shop.Actor.RoleTitle = roleTitle;
        shop.Actor.Role = role;
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Payments.Add(new Payment { Amount = 10m });
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        using var read = shop.Scope();
        var readDb = SqliteShop.Get<ShopDb>(read);
        Assert.Equal(expected, (await readDb.Payments.AsNoTracking().SingleAsync()).CreatedByName);
        Assert.Equal("Ada", (await readDb.Orders.AsNoTracking().SingleAsync()).CreatedByName);
    }

    [Fact]
    public async Task TheRowTime_AndTheCalendarDate_ComeFromTheInjectedClockAndCalendar()
    {
        // 22:30 UTC on 2026-03-20 is already 2026-03-21 02:00 at UTC+3:30, which is Persian 1405/01/01.
        var zone = TimeZoneInfo.CreateCustomTimeZone("tz", new TimeSpan(3, 30, 0), "tz", "tz");
        await using var shop = await SqliteShop.CreateAsync(
            now: new DateTimeOffset(2026, 3, 20, 22, 30, 0, TimeSpan.Zero),
            calendar: new PersianChronoAuditCalendar(zone));
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        var row = (await shop.AuditRowsAsync()).Single();
        Assert.Equal(shop.Clock.GetUtcNow(), row.OccurredAtUtc);
        Assert.Equal(TimeSpan.Zero, row.OccurredAtUtc.Offset);
        Assert.Equal(14050101, row.CalendarDate);
    }

    [Fact]
    public async Task ASaveWithAuditRows_WakesTheHashWorker_AndASaveWithoutDoesNot()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var signal = shop.Provider.GetRequiredService<AuditSignalChannel>();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Noises.Add(new Noise());
            await db.SaveChangesAsync();
        }

        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in signal.WaitAsync(quiet.Token))
            {
            }
        });

        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order());
            await db.SaveChangesAsync();
        }

        using var loud = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var woke in signal.WaitAsync(loud.Token))
        {
            Assert.True(woke);
            break;
        }
    }

    [Fact]
    public async Task AuditRows_AreAssignedAnIncreasingSequenceNumber_AndAUniqueId()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.AddRange(new Order(), new Order(), new Order());
            await db.SaveChangesAsync();
        }

        var rows = await shop.AuditRowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(rows.Select(r => r.SequenceNumber).Order(), rows.Select(r => r.SequenceNumber));
        Assert.Equal(3, rows.Select(r => r.SequenceNumber).Distinct().Count());
        Assert.Equal(3, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r => Assert.True(r.SequenceNumber > 0));
    }
}
