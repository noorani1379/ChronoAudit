using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class AuditSearchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

    private static AuditSearchQuery Query(Action<Dictionary<string, object?>>? tweak = null, string? cursor = null, int limit = 50)
        => new("shop", T0, T0.AddHours(1), Cursor: cursor, Limit: limit);

    private static async Task<SqliteShop> SeededAsync()
    {
        var shop = await SqliteShop.CreateAsync();
        var a = AuditResourceKey.Of(Guid.NewGuid());
        var b = AuditResourceKey.Of(Guid.NewGuid());
        await shop.SeedAuditRowsAsync(
            (1, "Orders", a, AuditActions.AutoAdded),
            (2, "Receipts", b, AuditActions.AutoAdded),
            (3, "Orders", a, AuditActions.AutoModified),
            (4, "Orders", b, AuditActions.AutoModified),
            (5, "Orders", a, AuditActions.AutoSoftDeleted));
        return shop;
    }

    [Fact]
    public async Task Filters_NarrowByActorResourceActionAndTime()
    {
        await using var shop = await SeededAsync();
        var search = shop.Provider.GetRequiredService<IAuditSearch>();

        var all = (await search.SearchAsync(Query(), CancellationToken.None)).Value;
        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, all.Items.Select(r => r.Sequence));
        Assert.Null(all.NextCursor);

        var modified = (await search.SearchAsync(Query() with { Action = AuditActions.AutoModified }, CancellationToken.None)).Value;
        Assert.Equal(new long[] { 4, 3 }, modified.Items.Select(r => r.Sequence));

        var receipts = (await search.SearchAsync(Query() with { ResourceType = "Receipts" }, CancellationToken.None)).Value;
        Assert.Equal(new long[] { 2 }, receipts.Items.Select(r => r.Sequence));

        var byActor = (await search.SearchAsync(Query() with { ActorUserId = "user-1" }, CancellationToken.None)).Value;
        Assert.Equal(5, byActor.Items.Count);
        var nobody = (await search.SearchAsync(Query() with { ActorUserId = "someone-else" }, CancellationToken.None)).Value;
        Assert.Empty(nobody.Items);

        var byResource = (await search.SearchAsync(Query() with { ResourceId = all.Items.Single(r => r.Sequence == 4).ResourceId }, CancellationToken.None)).Value;
        Assert.Equal(new long[] { 4, 2 }, byResource.Items.Select(r => r.Sequence));

        var window = (await search.SearchAsync(Query() with { From = T0.AddSeconds(2), To = T0.AddSeconds(4) }, CancellationToken.None)).Value;
        Assert.Equal(new long[] { 4, 3, 2 }, window.Items.Select(r => r.Sequence));
    }

    [Fact]
    public async Task Search_IsNewestFirst_AndKeysetPaged_ReturningEveryRowOnce()
    {
        await using var shop = await SeededAsync();
        var search = shop.Provider.GetRequiredService<IAuditSearch>();
        var seen = new List<long>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = (await search.SearchAsync(Query(cursor: cursor, limit: 2), CancellationToken.None)).Value;
            seen.AddRange(page.Items.Select(r => r.Sequence));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task ARowCarriesTheChainFields()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 2);
        await ChainTestHelper.HashAsync(shop, shop.Provider.GetRequiredService<AuditChain>());
        var search = shop.Provider.GetRequiredService<IAuditSearch>();
        var now = shop.Clock.GetUtcNow();

        var page = (await search.SearchAsync(new AuditSearchQuery("shop", now.AddMinutes(-1), now.AddMinutes(1)), CancellationToken.None)).Value;

        Assert.Equal(2, page.Items.Count);
        Assert.NotNull(page.Items[0].PayloadHash);
        Assert.Equal(page.Items[1].PayloadHash, page.Items[0].PreviousHash);
        Assert.Equal("Orders", page.Items[0].ResourceType);
        Assert.Equal(AuditActions.AutoAdded, page.Items[0].Action);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("")]
    public async Task ABadCursor_IsRefused(string cursor)
    {
        await using var shop = await SeededAsync();
        var search = shop.Provider.GetRequiredService<IAuditSearch>();

        var result = await search.SearchAsync(Query(cursor: cursor), CancellationToken.None);

        Assert.Equal("ChronoAudit.InvalidCursor", result.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task ACursorOfAnotherKindOrWithoutANumber_IsRefused()
    {
        await using var shop = await SeededAsync();
        var search = shop.Provider.GetRequiredService<IAuditSearch>();

        foreach (var cursor in new[] { CursorCodec.Encode("x1:", "5"), CursorCodec.Encode("a1:", "abc"), CursorCodec.Encode("a1:", "-3") })
        {
            Assert.Equal("ChronoAudit.InvalidCursor", (await search.SearchAsync(Query(cursor: cursor), CancellationToken.None)).Error.Code);
        }

        Assert.True((await search.SearchAsync(Query(cursor: CursorCodec.Encode("a1:", "3")), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task TheRangeAndLimit_AreBounded()
    {
        await using var shop = await SeededAsync();
        var search = shop.Provider.GetRequiredService<IAuditSearch>();

        var tooLong = await search.SearchAsync(new AuditSearchQuery("shop", T0, T0.AddDays(32)), CancellationToken.None);
        Assert.Equal("ChronoAudit.Validation", tooLong.Error.Code);

        var longWithActor = await search.SearchAsync(new AuditSearchQuery("shop", T0, T0.AddDays(300), ActorUserId: "user-1"), CancellationToken.None);
        Assert.True(longWithActor.IsSuccess);
        var tooLongEvenWithActor = await search.SearchAsync(new AuditSearchQuery("shop", T0, T0.AddDays(367), ActorUserId: "user-1"), CancellationToken.None);
        Assert.Equal("ChronoAudit.Validation", tooLongEvenWithActor.Error.Code);

        Assert.Equal("ChronoAudit.Validation", (await search.SearchAsync(new AuditSearchQuery("shop", T0, T0), CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await search.SearchAsync(Query(limit: 0), CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await search.SearchAsync(Query(limit: 201), CancellationToken.None)).Error.Code);
        Assert.True((await search.SearchAsync(Query(limit: 200), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task AnUnknownModule_IsNotFound()
    {
        await using var shop = await SeededAsync();
        var search = shop.Provider.GetRequiredService<IAuditSearch>();

        var result = await search.SearchAsync(new AuditSearchQuery("nope", T0, T0.AddHours(1)), CancellationToken.None);

        Assert.Equal("Table.NotFound", result.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.NotFound, result.Error.Kind);
    }
}
