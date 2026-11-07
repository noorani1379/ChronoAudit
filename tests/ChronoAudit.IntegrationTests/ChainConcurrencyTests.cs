using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.IntegrationTests;

public abstract class ChainConcurrencyTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyLateCommit_JoinsChainAtEndAsync()
    {
        await using var db = await TestDatabase.CreateAsync(Fixture.Provider);
        var chain = db.Services.GetRequiredService<AuditChain>();

        // 1. Connection A begins a transaction and inserts an audit row without committing
        var scopeA = db.Scope();
        var shopA = db.Shop(scopeA);
        var txA = await shopA.Database.BeginTransactionAsync();

        var entryA = new AuditLogEntry
        {
            CorrelationId = Guid.NewGuid(),
            ActorUserId = "userA",
            Action = "create",
            ResourceType = "Orders",
            ResourceId = "A",
        };
        shopA.AuditLogEntries.Add(entryA);
        await shopA.SaveChangesAsync();
        var seqA = entryA.SequenceNumber;

        // 2. Connection B inserts and commits a row
        long seqB;
        using (var scopeB = db.Scope())
        {
            var shopB = db.Shop(scopeB);
            var entryB = new AuditLogEntry
            {
                CorrelationId = Guid.NewGuid(),
                ActorUserId = "userB",
                Action = "create",
                ResourceType = "Orders",
                ResourceId = "B",
            };
            shopB.AuditLogEntries.Add(entryB);
            await shopB.SaveChangesAsync();
            seqB = entryB.SequenceNumber;
        }

        Assert.True(seqB > seqA, $"Expected seqB ({seqB}) > seqA ({seqA})");

        // 3. Run hasher: must hash row B only and NOT wait/hang on A
        using (var scopeH = db.Scope())
        {
            var shopH = db.Shop(scopeH);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var hashed = await chain.HashAllPendingAsync(shopH, batchSize: 10, cts.Token);
            Assert.Equal(1, hashed);

            var rowB = await shopH.AuditLogEntries.AsNoTracking().SingleAsync(x => x.SequenceNumber == seqB);
            Assert.Equal(1, rowB.ChainIndex);
            Assert.NotNull(rowB.PayloadHash);
        }

        // 4. Commit transaction A
        await txA.CommitAsync();
        scopeA.Dispose();

        // 5. Run hasher again: row A gets the next chain index (2)
        using (var scopeH = db.Scope())
        {
            var shopH = db.Shop(scopeH);
            var hashed = await chain.HashAllPendingAsync(shopH, batchSize: 10, CancellationToken.None);
            Assert.Equal(1, hashed);

            var rowA = await shopH.AuditLogEntries.AsNoTracking().SingleAsync(x => x.SequenceNumber == seqA);
            Assert.Equal(2, rowA.ChainIndex);
            Assert.NotNull(rowA.PayloadHash);

            var verify = await chain.VerifyAsync(shopH, CancellationToken.None);
            Assert.True(verify.IsValid, $"Chain should be valid: {verify.Failure}");
            Assert.Equal(2, verify.VerifiedCount);
            Assert.Equal(0, verify.PendingCount);
        }
    }

    protected async Task VerifyParallelWriters_ThenConcurrentHashers_LeaveOneValidChainAsync()
    {
        var db = Fixture.Database;
        var chain = db.Services.GetRequiredService<AuditChain>();

        // 64 parallel writers insert audit rows
        await Parallel.ForEachAsync(Enumerable.Range(0, 64), async (i, ct) =>
        {
            using var scope = db.Scope();
            var shop = db.Shop(scope);
            shop.Orders.Add(new Order { Title = $"Order {i}", Quantity = i });
            await shop.SaveChangesAsync(ct);
        });

        // Two hashers run concurrently on two connections
        var t1 = Task.Run(async () =>
        {
            using var scope1 = db.Scope();
            var shop1 = db.Shop(scope1);
            return await chain.HashAllPendingAsync(shop1, batchSize: 7, CancellationToken.None);
        });

        var t2 = Task.Run(async () =>
        {
            using var scope2 = db.Scope();
            var shop2 = db.Shop(scope2);
            return await chain.HashAllPendingAsync(shop2, batchSize: 7, CancellationToken.None);
        });

        await Task.WhenAll(t1, t2);

        // Verify valid chain
        using var scope = db.Scope();
        var shop = db.Shop(scope);
        var verify = await chain.VerifyAsync(shop, CancellationToken.None);
        Assert.True(verify.IsValid, $"Chain should be valid after concurrent hashing: {verify.Failure}");
        Assert.True(verify.VerifiedCount >= 64, $"VerifiedCount should be at least 64, got {verify.VerifiedCount}");
        Assert.Equal(0, verify.PendingCount);

        var chainIndices = await shop.AuditLogEntries
            .AsNoTracking()
            .Where(x => x.ChainIndex != null)
            .Select(x => x.ChainIndex!.Value)
            .ToListAsync();
        Assert.Equal(chainIndices.Distinct().Count(), chainIndices.Count);
    }

    protected async Task VerifyReaderDuringHashing_IsNeverDeadlockVictimAsync()
    {
        var db = Fixture.Database;
        var chain = db.Services.GetRequiredService<AuditChain>();

        // Seed 30 audit rows
        for (var i = 0; i < 30; i++)
        {
            using var scope = db.Scope();
            var shop = db.Shop(scope);
            shop.Orders.Add(new Order { Title = $"Seed {i}", Quantity = i });
            await shop.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Background hasher
        var hashingTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var scope = db.Scope();
                    var shop = db.Shop(scope);
                    var count = await chain.HashAllPendingAsync(shop, batchSize: 2, cts.Token);
                    if (count == 0)
                    {
                        await Task.Delay(20, cts.Token);
                    }
                }
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                // Our own stop signal can surface as the driver's "operation cancelled by user" (a SqlException, not an OperationCanceledException)
                // when it lands in the middle of a command. A failure before the stop signal is still thrown and fails the test.
            }
        }, cts.Token);

        // Reader queries 200 times
        for (var i = 0; i < 200; i++)
        {
            using var scope = db.Scope();
            var shop = db.Shop(scope);
            var rows = await shop.AuditLogEntries.AsNoTracking().Take(10).ToListAsync(CancellationToken.None);
            Assert.NotNull(rows);
        }

        cts.Cancel();
        try
        {
            await hashingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

[Trait("Category", "Integration")]
public sealed class SqlServerChainConcurrencyTests(SqlServerDatabaseFixture fixture)
    : ChainConcurrencyTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task LateCommit_JoinsChainAtEnd() => VerifyLateCommit_JoinsChainAtEndAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ParallelWriters_ThenConcurrentHashers_LeaveOneValidChain() => VerifyParallelWriters_ThenConcurrentHashers_LeaveOneValidChainAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task ReaderDuringHashing_IsNeverDeadlockVictim() => VerifyReaderDuringHashing_IsNeverDeadlockVictimAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlChainConcurrencyTests(PostgreSqlDatabaseFixture fixture)
    : ChainConcurrencyTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task LateCommit_JoinsChainAtEnd() => VerifyLateCommit_JoinsChainAtEndAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ParallelWriters_ThenConcurrentHashers_LeaveOneValidChain() => VerifyParallelWriters_ThenConcurrentHashers_LeaveOneValidChainAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task ReaderDuringHashing_IsNeverDeadlockVictim() => VerifyReaderDuringHashing_IsNeverDeadlockVictimAsync();
}
