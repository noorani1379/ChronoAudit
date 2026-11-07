using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

internal static class ChainTestHelper
{
    public static readonly byte[] KeyA = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");

    public static readonly byte[] KeyB = Encoding.UTF8.GetBytes("fedcba9876543210fedcba9876543210");

    /// <summary>Saves <paramref name="count"/> orders, one save each, so each gives one audit row.</summary>
    public static async Task AddOrdersAsync(SqliteShop shop, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var scope = shop.Scope();
            var db = SqliteShop.Get<ShopDb>(scope);
            db.Orders.Add(new Order { Title = "order " + i });
            await db.SaveChangesAsync();
        }
    }

    public static async Task<int> HashAsync(SqliteShop shop, AuditChain chain, int batchSize = 500)
    {
        using var scope = shop.Scope();
        return await chain.HashAllPendingAsync(SqliteShop.Get<ShopDb>(scope), batchSize, CancellationToken.None, "shop");
    }

    public static async Task<AuditChainVerification> VerifyAsync(SqliteShop shop, AuditChain chain, int batchSize = 2000, long after = 0, int maxRows = int.MaxValue)
    {
        using var scope = shop.Scope();
        return await chain.VerifyAsync(SqliteShop.Get<ShopDb>(scope), CancellationToken.None, batchSize, after, maxRows);
    }

    public static AuditChain ChainOf(byte[] key, params byte[][] previous) => new(new AuditKeyRing(key, previous));
}

public sealed class ChainTests
{
    [Fact]
    public async Task TheChain_IsBuiltInBatches_AndVerifiesFromTheStart()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 5);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);

        var hashed = await ChainTestHelper.HashAsync(shop, chain, batchSize: 2);

        Assert.Equal(5, hashed);
        var verification = await ChainTestHelper.VerifyAsync(shop, chain);
        Assert.True(verification.IsValid);
        Assert.Equal(5, verification.VerifiedCount);
        Assert.Equal(0, verification.PendingCount);
        var rows = await shop.AuditRowsAsync();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, rows.Select(r => r.ChainIndex!.Value));
        Assert.Null(rows[0].PreviousHash);
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.Equal(rows[i - 1].PayloadHash, rows[i].PreviousHash);
        }

        Assert.Equal(0, await ChainTestHelper.HashAsync(shop, chain));
    }

    [Fact]
    public async Task AnEditedRow_IsAHashMismatch_AtItsSequence()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 5);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await ChainTestHelper.HashAsync(shop, chain);
        var third = (await shop.AuditRowsAsync())[2];

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.SequenceNumber == third.SequenceNumber)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.NewValues, "{\"Title\":\"forged\"}"));
        }

        var verification = await ChainTestHelper.VerifyAsync(shop, chain);
        Assert.False(verification.IsValid);
        Assert.Equal(3, verification.FailedAtChainIndex);
        Assert.Equal(third.SequenceNumber, verification.FailedAtSequence);
        Assert.Equal(AuditChainFailure.HashMismatch, verification.Failure);
        Assert.Equal(2, verification.VerifiedCount);
    }

    [Fact]
    public async Task ADeletedMiddleRow_IsABrokenLink_AtTheNextRow()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 5);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await ChainTestHelper.HashAsync(shop, chain);

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 2).ExecuteDeleteAsync();
        }

        var verification = await ChainTestHelper.VerifyAsync(shop, chain);
        Assert.False(verification.IsValid);
        Assert.Equal(AuditChainFailure.BrokenLink, verification.Failure);
        Assert.Equal(2, verification.FailedAtChainIndex);
        Assert.Null(verification.FailedAtSequence);
        Assert.Equal(1, verification.VerifiedCount);
    }

    [Fact]
    public async Task ARewrittenLink_IsABrokenLink_AtThatRow()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await ChainTestHelper.HashAsync(shop, chain);

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 3)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.PreviousHash, "ABCDEF"));
        }

        var verification = await ChainTestHelper.VerifyAsync(shop, chain);
        Assert.Equal(AuditChainFailure.BrokenLink, verification.Failure);
        Assert.Equal(3, verification.FailedAtChainIndex);
        Assert.NotNull(verification.FailedAtSequence);
    }

    [Fact]
    public async Task UnhashedRows_ArePending_NotBroken()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await ChainTestHelper.HashAsync(shop, chain);
        await ChainTestHelper.AddOrdersAsync(shop, 2);

        var verification = await ChainTestHelper.VerifyAsync(shop, chain);

        Assert.True(verification.IsValid);
        Assert.Equal(3, verification.VerifiedCount);
        Assert.Equal(2, verification.PendingCount);
    }

    [Fact]
    public async Task VerifyInSteps_CoversEverything_AndFindsAnEditAfterABoundary()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 7);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await ChainTestHelper.HashAsync(shop, chain);

        long verified = 0;
        long? next = 0;
        var steps = 0;
        while (next is not null)
        {
            var step = await ChainTestHelper.VerifyAsync(shop, chain, batchSize: 2, after: next.Value, maxRows: 3);
            Assert.True(step.IsValid);
            verified += step.VerifiedCount;
            next = step.NextAfterChainIndex;
            steps++;
        }

        Assert.Equal(7, verified);
        Assert.Equal(3, steps);

        using (var scope = shop.Scope())
        {
            await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.Where(a => a.ChainIndex == 5)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Action, "forged"));
        }

        var first = await ChainTestHelper.VerifyAsync(shop, chain, maxRows: 3);
        Assert.True(first.IsValid);
        var second = await ChainTestHelper.VerifyAsync(shop, chain, after: first.NextAfterChainIndex!.Value, maxRows: 3);
        Assert.False(second.IsValid);
        Assert.Equal(5, second.FailedAtChainIndex);
    }

    [Fact]
    public async Task VerifyAfter_AMissingBoundaryRow_IsABrokenLink()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await ChainTestHelper.HashAsync(shop, chain);

        var verification = await ChainTestHelper.VerifyAsync(shop, chain, after: 99);

        Assert.Equal(AuditChainFailure.BrokenLink, verification.Failure);
        Assert.Equal(100, verification.FailedAtChainIndex);
    }

    [Fact]
    public void TheAuditHashLayout_IsPinned()
    {
        // The expected values were computed independently: HMAC-SHA256 over "prev|seq|correlation|actor|action|type|id|old|new|meta|date|"
        // with an empty field as just "|". Changing them means changing every stored chain (trap T7).
        var correlation1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var row1 = Row(1, correlation1, "u1", "Auto_Added", null, "{\"Title\":\"a\"}", null, at);
        var row2 = Row(2, correlation1, "u2", "Auto_Modified", "{\"Title\":\"a\"}", "{\"Title\":\"b\"}", "{\"Ip\":\"10.0.0.1\"}", at);
        var row3 = Row(3, Guid.Parse("00000000-0000-0000-0000-000000000002"), "u1", "Auto_Deleted", "{\"Title\":\"b\"}", null, null, at);
        using var hmac = System.Security.Cryptography.IncrementalHash.CreateHMAC(System.Security.Cryptography.HashAlgorithmName.SHA256, ChainTestHelper.KeyA);

        var h1 = AuditChain.ComputeHash(row1, null, hmac);
        var h2 = AuditChain.ComputeHash(row2, h1, hmac);
        var h3 = AuditChain.ComputeHash(row3, h2, hmac);

        Assert.Equal("5FD442EB2CDDF13B9C9844BEA1768DCD5DAB6A5282ACB14F6B6DFB677A913437", h1);
        Assert.Equal("5A61E22799280BB2601D488C1EC9B4027D38275811838C1DF3AAB6FF0B042A73", h2);
        Assert.Equal("C2B3701A64381E2581B8BC6CE5D59A9D3912D5460C92410DEECCABFB238F58F0", h3);
    }

    [Fact]
    public void TheHash_DependsOnTheKey_AndOnEveryHashedField_ButNotOnTheCalendarDate()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var row = Row(1, Guid.Empty, "u1", "Auto_Added", null, "{}", null, at);
        using var a = System.Security.Cryptography.IncrementalHash.CreateHMAC(System.Security.Cryptography.HashAlgorithmName.SHA256, ChainTestHelper.KeyA);
        using var b = System.Security.Cryptography.IncrementalHash.CreateHMAC(System.Security.Cryptography.HashAlgorithmName.SHA256, ChainTestHelper.KeyB);

        var withA = AuditChain.ComputeHash(row, null, a);

        Assert.NotEqual(withA, AuditChain.ComputeHash(row, null, b));
        Assert.NotEqual(withA, AuditChain.ComputeHash(Row(1, Guid.Empty, "u1", "Auto_Added", null, "{ }", null, at), null, a));
        Assert.NotEqual(withA, AuditChain.ComputeHash(row, "ABC", a));

        // CalendarDate is not hashed (the layout is frozen): stamping another calendar date changes nothing.
        var sameRowOtherDate = Row(1, Guid.Empty, "u1", "Auto_Added", null, "{}", null, at);
        sameRowOtherDate.StampTime(at, 14040101);
        Assert.Equal(withA, AuditChain.ComputeHash(sameRowOtherDate, null, a));
    }

    [Fact]
    public async Task ALateRow_JoinsTheChainAtTheEnd()
    {
        await using var shop = await SqliteShop.CreateAsync();
        var chain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        await SeedWithSequenceAsync(shop, 10, 11);
        Assert.Equal(2, await ChainTestHelper.HashAsync(shop, chain));

        // A row that "committed late": lower sequence than the rows already hashed.
        await SeedWithSequenceAsync(shop, 5);
        Assert.Equal(1, await ChainTestHelper.HashAsync(shop, chain));

        var late = (await shop.AuditRowsAsync()).Single(r => r.SequenceNumber == 5);
        Assert.Equal(3, late.ChainIndex);
        Assert.True((await ChainTestHelper.VerifyAsync(shop, chain)).IsValid);

        // History paging by sequence is untouched.
        using var scope = shop.Scope();
        var bySequence = await SqliteShop.Get<ShopDb>(scope).AuditLogEntries.AsNoTracking().OrderBy(a => a.SequenceNumber).Select(a => a.SequenceNumber).ToListAsync();
        Assert.Equal(new long[] { 5, 10, 11 }, bySequence);
    }

    [Fact]
    public async Task AfterAKeyRotation_VerificationUsesEachSegmentsKey()
    {
        // The shop runs with key B and knows key A as a previous key.
        await using var shop = await SqliteShop.CreateAsync(o => o.PreviousHmacKeys.Add(ChainTestHelper.KeyA), key: ChainTestHelper.KeyB);
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        var oldChain = ChainTestHelper.ChainOf(ChainTestHelper.KeyA);
        Assert.Equal(3, await ChainTestHelper.HashAsync(shop, oldChain));

        await ChainTestHelper.AddOrdersAsync(shop, 1);
        var maintenance = shop.Provider.GetRequiredService<IChainMaintenance>();
        var rotated = await maintenance.RotateKeyAsync("shop", CancellationToken.None);
        Assert.True(rotated.IsSuccess, rotated.IsFailure ? rotated.Error.Message : null);

        var again = await maintenance.RotateKeyAsync("shop", CancellationToken.None);
        Assert.Equal("Chain.RotationPending", again.Error.Code);

        await ChainTestHelper.AddOrdersAsync(shop, 1);
        var chainB = shop.Provider.GetRequiredService<AuditChain>();
        Assert.Equal(3, await ChainTestHelper.HashAsync(shop, chainB));

        var rows = await shop.AuditRowsAsync();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, rows.Select(r => r.ChainIndex!.Value));
        Assert.Equal(AuditActions.KeyRotated, rows[4].Action);
        Assert.Equal("ChronoAudit.Chain", rows[4].ResourceType);
        Assert.Equal("shop", rows[4].ResourceId);
        Assert.Contains("\"BoundaryChainIndex\":3", rows[4].Metadata, StringComparison.Ordinal);
        Assert.Contains(ChronoAuditKey.Fingerprint(ChainTestHelper.KeyA), rows[4].Metadata, StringComparison.Ordinal);
        Assert.Contains(ChronoAuditKey.Fingerprint(ChainTestHelper.KeyB), rows[4].Metadata, StringComparison.Ordinal);

        var verification = await ChainTestHelper.VerifyAsync(shop, chainB);
        Assert.True(verification.IsValid);
        Assert.Equal(6, verification.VerifiedCount);

        // Rows 1-3 really carry key A and rows 4-6 key B: a ring without A cannot verify, a ring with only A fails at row 4.
        var withoutA = await ChainTestHelper.VerifyAsync(shop, ChainTestHelper.ChainOf(ChainTestHelper.KeyB));
        Assert.Equal(AuditChainFailure.UnknownKey, withoutA.Failure);
        Assert.Equal(1, withoutA.FailedAtChainIndex);
        var onlyA = await ChainTestHelper.VerifyAsync(shop, ChainTestHelper.ChainOf(ChainTestHelper.KeyA, ChainTestHelper.KeyB));
        Assert.True(onlyA.IsValid);

        var afterHashing = await maintenance.RotateKeyAsync("shop", CancellationToken.None);
        Assert.Equal("Chain.NothingToRotate", afterHashing.Error.Code);
    }

    [Fact]
    public async Task Rotation_Refuses_UnknownModules_EmptyChains_AndUnknownKeys()
    {
        await using var shop = await SqliteShop.CreateAsync(key: ChainTestHelper.KeyB);
        var maintenance = shop.Provider.GetRequiredService<IChainMaintenance>();

        var unknown = await maintenance.RotateKeyAsync("nope", CancellationToken.None);
        Assert.Equal("Chain.UnknownModule", unknown.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.NotFound, unknown.Error.Kind);

        var empty = await maintenance.RotateKeyAsync("shop", CancellationToken.None);
        Assert.Equal("Chain.NothingToRotate", empty.Error.Code);

        // Rows hashed with key A, but A is not in the ring (and B is current).
        await ChainTestHelper.AddOrdersAsync(shop, 2);
        await ChainTestHelper.HashAsync(shop, ChainTestHelper.ChainOf(ChainTestHelper.KeyA));
        var lost = await maintenance.RotateKeyAsync("shop", CancellationToken.None);
        Assert.Equal("Chain.UnknownKey", lost.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Conflict, lost.Error.Kind);
    }

    [Fact]
    public async Task AKeyChangeWithoutRotation_StopsHashing_AndLogsCritical()
    {
        await using var shop = await SqliteShop.CreateAsync(key: ChainTestHelper.KeyB);
        await ChainTestHelper.AddOrdersAsync(shop, 3);
        await ChainTestHelper.HashAsync(shop, ChainTestHelper.ChainOf(ChainTestHelper.KeyA));

        // The key was changed to B without a rotation: hashing on would mix two keys in one segment.
        await ChainTestHelper.AddOrdersAsync(shop, 2);
        var chainB = shop.Provider.GetRequiredService<AuditChain>();
        var result = await ChainTestHelper.HashAsync(shop, chainB);

        Assert.Equal(-1, result);
        var rows = await shop.AuditRowsAsync();
        Assert.Equal(2, rows.Count(r => r.PayloadHash is null));
        var critical = Assert.Single(shop.Logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("hashed with another key", critical.Message, StringComparison.Ordinal);
        Assert.Contains("chain index 3", critical.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("order", critical.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetString(ChainTestHelper.KeyB), critical.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyRing_FindsCurrentAndPreviousKeys_ByFingerprint()
    {
        var ring = new AuditKeyRing(ChainTestHelper.KeyB, [ChainTestHelper.KeyA, ChainTestHelper.KeyB]);

        Assert.Equal(ChronoAuditKey.Fingerprint(ChainTestHelper.KeyB), ring.CurrentFingerprint);
        Assert.True(ring.TryGet(ChronoAuditKey.Fingerprint(ChainTestHelper.KeyA), out var a));
        Assert.Equal(ChainTestHelper.KeyA, a);
        Assert.True(ring.TryGet(ring.CurrentFingerprint, out var b));
        Assert.Equal(ChainTestHelper.KeyB, b);
        Assert.False(ring.TryGet("FFFFFFFF", out _));
        Assert.Equal(2, ring.Fingerprints.Count());
    }

    private static AuditLogEntry Row(long sequence, Guid correlation, string actor, string action, string? oldValues, string? newValues, string? metadata, DateTimeOffset at)
    {
        var entry = new AuditLogEntry
        {
            CorrelationId = correlation,
            ActorUserId = actor,
            Action = action,
            ResourceType = "Orders",
            ResourceId = "{\"Id\":\"1\"}",
            OldValues = oldValues,
            NewValues = newValues,
            Metadata = metadata,
        };
        typeof(AuditLogEntry).GetProperty(nameof(AuditLogEntry.SequenceNumber))!.SetValue(entry, sequence);
        entry.StampTime(at, 20260101);
        return entry;
    }

    private static async Task SeedWithSequenceAsync(SqliteShop shop, params long[] sequences)
    {
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        db.BypassAuditingForSeed = true;
        foreach (var sequence in sequences)
        {
            var entry = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = "user-1",
                Action = AuditActions.AutoAdded,
                ResourceType = "Orders",
                ResourceId = "{\"Id\":" + sequence + "}",
                NewValues = "{\"Title\":\"row " + sequence + "\"}",
            };
            entry.StampTime(new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence), 20261031);
            db.AuditLogEntries.Add(entry);
            db.Entry(entry).Property(nameof(AuditLogEntry.SequenceNumber)).CurrentValue = sequence;
        }

        await db.SaveChangesAsync();
    }
}
