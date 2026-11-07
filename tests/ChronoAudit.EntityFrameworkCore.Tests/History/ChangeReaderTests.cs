using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class ChangeReaderTests
{
    private static readonly ChronoAuditScope All = ChronoAuditScope.Everything("auditor-1");

    private sealed class Policy(bool allow) : IPersonalDataRevealPolicy
    {
        public int Asked { get; private set; }

        public Task<bool> MayRevealAsync(ChronoAuditScope scope, string resourceType, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(allow);
        }
    }

    private sealed class ThrowingWriter(Exception error) : IAuditLogWriter
    {
        public Task AppendHashedAsync(string actorUserId, string action, string resourceType, string resourceId, object? metadata = null, CancellationToken cancellationToken = default)
            => Task.FromException(error);
    }

    private static Task<SqliteShop> ShopAsync(Policy? policy = null, Action<IServiceCollection>? services = null)
        => SqliteShop.CreateAsync(
            o =>
            {
                o.PersonalData.NumberFields.Add("PhoneNumber");
                o.PersonalData.TextFields.Add("FullName");
            },
            customize: b =>
            {
                if (policy is not null)
                {
                    b.Services.AddSingleton<IPersonalDataRevealPolicy>(policy);
                }

                services?.Invoke(b.Services);
            });

    private static async Task<Guid> AddProfileAsync(SqliteShop shop, string phone = "0012345678", string name = "Ada Lovelace")
    {
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        var profile = new CustomerProfile { PhoneNumber = phone, FullName = name, City = "London", Pin = "1234" };
        db.CustomerProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static async Task ChangeProfileAsync(SqliteShop shop, Guid id, string phone, string city)
    {
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        var profile = await db.CustomerProfiles.SingleAsync(p => p.Id == id);
        profile.PhoneNumber = phone;
        profile.City = city;
        await db.SaveChangesAsync();
    }

    private static async Task<ChronoAuditResult<ChangePage>> ReadRecordAsync(SqliteShop shop, Guid id, bool reveal = false, string? cursor = null, int limit = 50, ChronoAuditScope? scope = null)
    {
        using var s = shop.Scope();
        return await SqliteShop.Get<IChangeReader>(s).RecordAsync("shop", "CustomerProfiles", id.ToString(), scope ?? All, cursor, limit, reveal, CancellationToken.None);
    }

    [Fact]
    public async Task AModifiedRow_ListsOnlyChangedFields_AndACreatedRowHasNoOld()
    {
        await using var shop = await ShopAsync();
        var id = await AddProfileAsync(shop);
        shop.Clock.Advance(TimeSpan.FromMinutes(1));
        await ChangeProfileAsync(shop, id, "0012345678", "Paris");

        var page = (await ReadRecordAsync(shop, id, reveal: true)).Value;

        Assert.Equal(2, page.Items.Count);
        var created = page.Items[0];
        Assert.Equal(AuditActions.AutoAdded, created.Action);
        Assert.All(created.Fields, f => Assert.Null(f.From));
        Assert.Contains(created.Fields, f => f.Field == "City" && f.To!.Value.GetString() == "London");
        var modified = page.Items[1];
        var changed = Assert.Single(modified.Fields, f => f.Field == "City");
        Assert.Equal("London", changed.From!.Value.GetString());
        Assert.Equal("Paris", changed.To!.Value.GetString());
        Assert.DoesNotContain(modified.Fields, f => f.Field == "FullName");
        Assert.Equal("CustomerProfiles", modified.ResourceType);
        Assert.Equal(id.ToString(), modified.RecordKey);
    }

    [Fact]
    public async Task Actions_AreShownAsStored_AndTheDelegationIsShown()
    {
        await using var shop = await ShopAsync();
        shop.Actor.IsDelegated = true;
        shop.Actor.ActorUserId = "agent-9";
        shop.Actor.AuditMetadata = new { ActorDisplayName = "Agent Nine", OnBehalfOfUserId = "owner-1" };
        var id = await AddProfileAsync(shop);
        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<IAuditLogWriter>(scope).AppendHashedAsync("agent-9", "ProfileViewed", "CustomerProfiles", AuditResourceKey.Of(id));
            await SqliteShop.Get<ShopDb>(scope).SaveChangesAsync();
        }

        var page = (await ReadRecordAsync(shop, id)).Value;

        Assert.Equal(new[] { AuditActions.AutoAdded }, page.Items.Select(i => i.Action).Take(1));
        var row = page.Items[0];
        Assert.Equal("agent-9", row.ActorUserId);
        Assert.Equal("Agent Nine", row.ActorDisplayName);
        Assert.Equal("owner-1", row.OnBehalfOfUserId);
    }

    [Fact]
    public async Task Paging_HasNoGapAndNoRepeat()
    {
        await using var shop = await ShopAsync();
        var id = await AddProfileAsync(shop);
        for (var i = 0; i < 4; i++)
        {
            shop.Clock.Advance(TimeSpan.FromMinutes(1));
            await ChangeProfileAsync(shop, id, "0012345678", "City " + i);
        }

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = (await ReadRecordAsync(shop, id, cursor: cursor, limit: 2)).Value;
            seen.AddRange(page.Items.Select(i => i.Sequence));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Order(), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());

        // The same for a whole table.
        using var scope = shop.Scope();
        var reader = SqliteShop.Get<IChangeReader>(scope);
        var table = new List<long>();
        cursor = null;
        do
        {
            var page = (await reader.TableAsync("shop", "CustomerProfiles", All, null, null, cursor, 2, false, CancellationToken.None)).Value;
            table.AddRange(page.Items.Select(i => i.Sequence));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(seen, table);
    }

    [Theory]
    [InlineData("not a cursor")]
    [InlineData("")]
    public async Task ABadCursor_IsRefusedBeforeAnyRead(string cursor)
    {
        await using var shop = await ShopAsync(new Policy(true));
        var id = await AddProfileAsync(shop);

        var record = await ReadRecordAsync(shop, id, reveal: true, cursor: cursor);

        Assert.Equal("ChronoAudit.InvalidCursor", record.Error.Code);
        Assert.DoesNotContain(await shop.AuditRowsAsync(), r => r.Action == AuditActions.PersonalDataRevealed);
    }

    [Fact]
    public async Task PersonalFields_AreMaskedByDefault()
    {
        await using var shop = await ShopAsync();
        var id = await AddProfileAsync(shop);

        var page = (await ReadRecordAsync(shop, id, reveal: false)).Value;

        Assert.False(page.PersonalDataRevealed);
        var fields = page.Items[0].Fields.ToDictionary(f => f.Field, f => f.To!.Value.ToString());
        Assert.Equal("0012****78", fields["PhoneNumber"]);
        Assert.Equal("***", fields["FullName"]);
        Assert.Equal("London", fields["City"]);
        Assert.Equal("***MASKED***", fields["Pin"]);
        Assert.Equal(shop.Clock.GetUtcNow(), page.AsOfUtc);
    }

    [Fact]
    public async Task PersonalFields_AreMasked_EvenWhenRevealIsAskedForAndRefused()
    {
        var policy = new Policy(false);
        await using var shop = await ShopAsync(policy);
        var id = await AddProfileAsync(shop);

        var page = (await ReadRecordAsync(shop, id, reveal: true)).Value;

        Assert.False(page.PersonalDataRevealed);
        Assert.Equal(1, policy.Asked);
        Assert.Equal("***", page.Items[0].Fields.Single(f => f.Field == "FullName").To!.Value.GetString());
    }

    [Fact]
    public async Task Reveal_AllowedAndAudited_ShowsWholeValues_AndWritesOneRow()
    {
        var policy = new Policy(true);
        await using var shop = await ShopAsync(policy);
        var id = await AddProfileAsync(shop);
        await ChangeProfileAsync(shop, id, "0099999999", "Paris");

        var page = (await ReadRecordAsync(shop, id, reveal: true)).Value;

        Assert.True(page.PersonalDataRevealed);
        Assert.Equal(1, policy.Asked);
        Assert.Equal("0012345678", page.Items[0].Fields.Single(f => f.Field == "PhoneNumber").To!.Value.GetString());
        Assert.Equal("Ada Lovelace", page.Items[0].Fields.Single(f => f.Field == "FullName").To!.Value.GetString());
        Assert.Equal("***MASKED***", page.Items[0].Fields.Single(f => f.Field == "Pin").To!.Value.GetString());

        var reveals = (await shop.AuditRowsAsync()).Where(r => r.Action == AuditActions.PersonalDataRevealed).ToList();
        var row = Assert.Single(reveals);
        Assert.Equal("auditor-1", row.ActorUserId);
        Assert.Equal("CustomerProfiles", row.ResourceType);
        Assert.Equal(id.ToString(), row.ResourceId);
        Assert.Contains("\"viewer\":\"auditor-1\"", row.Metadata, StringComparison.Ordinal);
        Assert.Contains("PhoneNumber", row.Metadata, StringComparison.Ordinal);
        Assert.Contains("FullName", row.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reveal_Refused_StaysMasked_AndNoPersonalFieldsNeverAsks()
    {
        var policy = new Policy(true);
        await using var shop = await ShopAsync(policy);
        var id = await AddProfileAsync(shop);

        // A page without personal fields: the policy is not asked and no audit row is written.
        using (var scope = shop.Scope())
        {
            var db = SqliteShop.Get<ShopDb>(scope);
            var order = new Order { Title = "chair" };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            var page = (await SqliteShop.Get<IChangeReader>(scope).RecordAsync("shop", "Orders", order.Id.ToString(), All, null, 10, true, CancellationToken.None)).Value;
            Assert.False(page.PersonalDataRevealed);
        }

        Assert.Equal(0, policy.Asked);
        Assert.DoesNotContain(await shop.AuditRowsAsync(), r => r.Action == AuditActions.PersonalDataRevealed);

        // Reveal not asked for: not asked of the policy either.
        await ReadRecordAsync(shop, id, reveal: false);
        Assert.Equal(0, policy.Asked);
    }

    [Fact]
    public async Task Reveal_WhenTheAuditFails_StaysMasked()
    {
        var policy = new Policy(true);
        await using var shop = await ShopAsync(policy, s => s.AddScoped<IAuditLogWriter>(_ => new ThrowingWriter(new InvalidOperationException("db down"))));
        var id = await AddProfileAsync(shop);

        var page = (await ReadRecordAsync(shop, id, reveal: true)).Value;

        Assert.False(page.PersonalDataRevealed);
        Assert.Equal("***", page.Items[0].Fields.Single(f => f.Field == "FullName").To!.Value.GetString());
        Assert.Contains(shop.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("returned masked", StringComparison.Ordinal));
        Assert.DoesNotContain(await shop.AuditRowsAsync(), r => r.Action == AuditActions.PersonalDataRevealed);
    }

    [Fact]
    public async Task Reveal_Cancellation_Propagates()
    {
        await using var shop = await ShopAsync(new Policy(true), s => s.AddScoped<IAuditLogWriter>(_ => new ThrowingWriter(new OperationCanceledException())));
        var id = await AddProfileAsync(shop);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadRecordAsync(shop, id, reveal: true));
    }

    [Fact]
    public async Task TableHistory_IsForTheAllScopeOnly_AndEveryOtherScopeGetsTheSameAnswerAsAnUnknownTable()
    {
        await using var shop = await ShopAsync();
        await AddProfileAsync(shop);
        using var scope = shop.Scope();
        var reader = SqliteShop.Get<IChangeReader>(scope);

        var unknown = await reader.TableAsync("shop", "Nope", All, null, null, null, 10, false, CancellationToken.None);
        var unknownModule = await reader.TableAsync("nope", "CustomerProfiles", All, null, null, null, 10, false, CancellationToken.None);
        Assert.Equal("Table.NotFound", unknown.Error.Code);
        Assert.Equal("Table.NotFound", unknownModule.Error.Code);

        foreach (var restricted in new[] { ChronoAuditScope.OwnOf("u1"), ChronoAuditScope.GroupOf("u1", "g1") })
        {
            var denied = await reader.TableAsync("shop", "CustomerProfiles", restricted, null, null, null, 10, false, CancellationToken.None);
            Assert.Equal(unknown.Error, denied.Error);
            var deniedRecord = await reader.RecordAsync("shop", "CustomerProfiles", Guid.NewGuid().ToString(), restricted, null, 10, false, CancellationToken.None);
            Assert.Equal(unknown.Error, deniedRecord.Error);
        }

        Assert.Equal("ChronoAudit.Validation", (await reader.TableAsync("shop", "CustomerProfiles", All, null, null, null, 0, false, CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await reader.TableAsync("shop", "CustomerProfiles", All, null, null, null, 201, false, CancellationToken.None)).Error.Code);
        var t = DateTimeOffset.UtcNow;
        Assert.Equal("ChronoAudit.Validation", (await reader.TableAsync("shop", "CustomerProfiles", All, t, t, null, 10, false, CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.InvalidCursor", (await reader.TableAsync("shop", "CustomerProfiles", All, null, null, "###", 10, false, CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task OneRecord_ReturnsOnlyItsRows_AndARecordWithNoHistoryIsNotFound_ButAnEmptyLaterPageIsNot()
    {
        await using var shop = await ShopAsync();
        var mine = await AddProfileAsync(shop);
        var other = await AddProfileAsync(shop, "0055555555", "Grace Hopper");

        var page = (await ReadRecordAsync(shop, mine)).Value;
        Assert.Single(page.Items);
        Assert.Equal(mine.ToString(), page.Items[0].RecordKey);
        Assert.DoesNotContain(other.ToString(), page.Items[0].RecordKey, StringComparison.Ordinal);

        var none = await ReadRecordAsync(shop, Guid.NewGuid());
        Assert.Equal("Record.NotFound", none.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.NotFound, none.Error.Kind);

        var later = (await ReadRecordAsync(shop, mine, cursor: CursorCodec.Encode("c1:", page.Items[0].Sequence.ToString())))
            .Value;
        Assert.Empty(later.Items);
        Assert.Null(later.NextCursor);
    }

    [Fact]
    public async Task ATableWhoseKeyCannotBeBuilt_IsAnExplicit422_NotASilentEmptyAnswer()
    {
        await using var shop = await ShopAsync();
        var id = await AddProfileAsync(shop);
        using var scope = shop.Scope();
        var reader = SqliteShop.Get<IChangeReader>(scope);

        var notAGuid = await reader.RecordAsync("shop", "CustomerProfiles", "not-a-guid", All, null, 10, false, CancellationToken.None);
        var tooManyParts = await reader.RecordAsync("shop", "CustomerProfiles", id + "|2", All, null, 10, false, CancellationToken.None);
        var compositeMissingPart = await reader.RecordAsync("shop", "OrderLines", Guid.NewGuid().ToString(), All, null, 10, false, CancellationToken.None);

        foreach (var result in new[] { notAGuid, tooManyParts, compositeMissingPart })
        {
            Assert.Equal("Table.RecordLookupNotSupported", result.Error.Code);
            Assert.Equal(ChronoAuditErrorKind.Unprocessable, result.Error.Kind);
        }
    }
}

public sealed class ChangeReaderModuleTests
{
    [Fact]
    public async Task EachModule_ReturnsItsOwnTable_ThroughItsOwnSource()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        await host.AddAsync("shop", "shop order");
        await host.AddAsync("billing", "billing order");
        using var scope = host.Provider.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IChangeReader>();
        var all = ChronoAuditScope.Everything(null);

        var shop = (await reader.TableAsync("shop", "Orders", all, null, null, null, 10, false, CancellationToken.None)).Value;
        var billing = (await reader.TableAsync("BILLING", "Orders", all, null, null, null, 10, false, CancellationToken.None)).Value;

        Assert.Equal("shop order", Assert.Single(shop.Items).Fields.Single(f => f.Field == "Title").To!.Value.GetString());
        Assert.Equal("billing order", Assert.Single(billing.Items).Fields.Single(f => f.Field == "Title").To!.Value.GetString());
        Assert.Equal("Table.NotFound", (await reader.TableAsync("billing", "OrderLines", all, null, null, null, 10, false, CancellationToken.None)).Error.Code);
    }
}
