using ChronoAudit.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class DialectTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "SqlServer", "[PayloadHash] IS NULL", "[ChainIndex] IS NOT NULL")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSql", "\"PayloadHash\" IS NULL", "\"ChainIndex\" IS NOT NULL")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "Sqlite", "\"PayloadHash\" IS NULL", "\"ChainIndex\" IS NOT NULL")]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory", "InMemory", "\"PayloadHash\" IS NULL", "\"ChainIndex\" IS NOT NULL")]
    [InlineData("Some.Other.Provider", "Unknown", "\"PayloadHash\" IS NULL", "\"ChainIndex\" IS NOT NULL")]
    [InlineData(null, "Unknown", "\"PayloadHash\" IS NULL", "\"ChainIndex\" IS NOT NULL")]
    public void FilterSql_IsQuotedPerProvider(string? provider, string kind, string isNull, string isNotNull)
    {
        var dialect = ChronoAuditDialect.For(provider);

        Assert.Equal(Enum.Parse<ProviderKind>(kind), dialect.Kind);
        Assert.Equal(isNull, dialect.IsNull("PayloadHash"));
        Assert.Equal(isNotNull, dialect.IsNotNull("ChainIndex"));
    }

    [Fact]
    public async Task TheModelFilters_AreBuiltThroughTheDialect()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var entity = SqliteShop.Get<ShopDb>(scope).Model.FindEntityType(typeof(AuditLogEntry))!;

        var unhashed = entity.GetIndexes().Single(i => i.GetDatabaseName() == "IX_AuditLogEntries_Unhashed");
        var chainIndex = entity.GetIndexes().Single(i => i.GetDatabaseName() == "UX_AuditLogEntries_ChainIndex");

        Assert.Equal("\"PayloadHash\" IS NULL", unhashed.GetFilter());
        Assert.Equal("\"ChainIndex\" IS NOT NULL", chainIndex.GetFilter());
    }

    [Fact]
    public async Task UniqueViolation_IsRecognized_OnSqlite()
    {
        await using var shop = await SqliteShop.CreateAsync();
        await shop.SeedAuditRowsAsync((1, "Orders", "{\"Id\":1}", AuditActions.AutoAdded));
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        db.BypassAuditingForSeed = true;
        var duplicate = new AuditLogEntry { CorrelationId = Guid.NewGuid(), ActorUserId = "u", Action = "x", ResourceType = "Orders", ResourceId = "{}" };
        duplicate.StampTime(DateTimeOffset.UtcNow, 20260101);
        db.AuditLogEntries.Add(duplicate);
        db.Entry(duplicate).Property(nameof(AuditLogEntry.SequenceNumber)).CurrentValue = 1L;

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var dialect = ChronoAuditDialect.For(db);
        Assert.True(dialect.IsUniqueViolation(ex));
        Assert.False(dialect.IsTransient(ex));
    }

    [Fact]
    public async Task ANotNullViolation_IsNotAUniqueViolation()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO AuditLogEntries (Id) VALUES (NULL)"));

        Assert.False(ChronoAuditDialect.For(db).IsUniqueViolation(ex));
    }

    [Fact]
    public void Exceptions_AreClassified_ByTheirShape()
    {
        var dialect = ChronoAuditDialect.For("Microsoft.EntityFrameworkCore.SqlServer");

        Assert.True(dialect.IsTransient(new TimeoutException()));
        Assert.True(dialect.IsTransient(new InvalidOperationException("outer", new TimeoutException())));
        Assert.False(dialect.IsTransient(new InvalidOperationException("plain")));
        Assert.False(dialect.IsUniqueViolation(new InvalidOperationException("plain")));
        Assert.True(dialect.IsTransient(new FakeDbException("40001")));
        Assert.True(dialect.IsTransient(new FakeDbException("40P01")));
        Assert.False(dialect.IsTransient(new FakeDbException("23505")));
        Assert.True(dialect.IsUniqueViolation(new DbUpdateException("x", new FakeDbException("23505"))));
        Assert.False(dialect.IsUniqueViolation(new FakeDbException("23503")));
    }

    [Fact]
    public async Task AnUnknownProvider_GetsTheSafeDefaults_AndAWarning()
    {
        var unknown = ChronoAuditDialect.For("Some.Other.Provider");
        Assert.Equal(ProviderKind.Unknown, unknown.Kind);
        Assert.Equal("\"X\" IS NULL", unknown.IsNull("X"));
        await unknown.AcquireChainLockAsync(new DbContext(new DbContextOptionsBuilder().UseInMemoryDatabase("x").Options), "lock", CancellationToken.None);

        // The startup log warns about a context whose provider the dialect does not know; SQLite is known, so it stays quiet.
        await using var shop = await SqliteShop.CreateAsync();
        var hosted = shop.Provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "ChronoAuditStartupLog");
        await hosted.StartAsync(CancellationToken.None);
        Assert.DoesNotContain(shop.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("does not know the database provider", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLockKey_IsStable_AndDiffersPerName()
    {
        Assert.Equal(ChronoAuditDialect.LockKey("chronoaudit:audit:shop"), ChronoAuditDialect.LockKey("chronoaudit:audit:shop"));
        Assert.NotEqual(ChronoAuditDialect.LockKey("chronoaudit:audit:shop"), ChronoAuditDialect.LockKey("chronoaudit:audit:billing"));
    }

    private sealed class FakeDbException(string? sqlState) : System.Data.Common.DbException("fake")
    {
        public override string? SqlState { get; } = sqlState;
    }
}

public sealed class ChainLockTests
{
    [Fact]
    public async Task TheLock_NeedsATransaction()
    {
        await using var shop = await SqliteShop.CreateAsync();
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);

        foreach (var kind in new[] { ProviderKind.SqlServer, ProviderKind.PostgreSql })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ChronoAuditDialect(kind).AcquireChainLockAsync(db, "lock", CancellationToken.None));
            Assert.Contains("open transaction", ex.Message, StringComparison.Ordinal);
        }

        // SQLite, InMemory and unknown providers return before that check.
        foreach (var kind in new[] { ProviderKind.Sqlite, ProviderKind.InMemory, ProviderKind.Unknown })
        {
            await new ChronoAuditDialect(kind).AcquireChainLockAsync(db, "lock", CancellationToken.None);
        }
    }
}

public sealed class GuardTriggerSqliteTests
{
    private static async Task ApplyGuardAsync(SqliteShop shop, Func<MigrationBuilder, MigrationBuilder> create)
    {
        using var scope = shop.Scope();
        var db = SqliteShop.Get<ShopDb>(scope);
        var builder = new MigrationBuilder(ChronoAuditDialect.SqliteProvider);
        create(builder);
        foreach (var operation in builder.Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
        {
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
        }
    }

    private static async Task<Exception?> TryAsync(SqliteShop shop, string sql)
    {
        using var scope = shop.Scope();
        try
        {
            await SqliteShop.Get<ShopDb>(scope).Database.ExecuteSqlRawAsync(sql);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<SqliteShop> GuardedShopAsync()
    {
        var shop = await SqliteShop.CreateAsync();
        await ApplyGuardAsync(shop, b => b.CreateChronoAuditAuditGuard());
        await shop.SeedAuditRowAsync("Orders", "{\"Id\":1}", AuditActions.AutoAdded);
        return shop;
    }

    [Fact]
    public async Task ADelete_IsRefused()
    {
        await using var shop = await GuardedShopAsync();

        var ex = await TryAsync(shop, "DELETE FROM AuditLogEntries");

        Assert.NotNull(ex);
        Assert.Contains("cannot be deleted", ex.Message, StringComparison.Ordinal);
        Assert.Single(await shop.AuditRowsAsync());
    }

    [Fact]
    public async Task TheFirstHash_CanBeSet_ButNotChanged()
    {
        await using var shop = await GuardedShopAsync();

        Assert.Null(await TryAsync(shop, "UPDATE AuditLogEntries SET PayloadHash = 'AA', PreviousHash = NULL, ChainIndex = 1"));
        var again = await TryAsync(shop, "UPDATE AuditLogEntries SET PayloadHash = 'BB'");

        Assert.NotNull(again);
        Assert.Contains("only the first hash", again.Message, StringComparison.Ordinal);
        Assert.Equal("AA", Assert.Single(await shop.AuditRowsAsync()).PayloadHash);
    }

    [Theory]
    [InlineData("Action = 'forged'")]
    [InlineData("ActorUserId = 'someone'")]
    [InlineData("NewValues = 'forged'")]
    [InlineData("CalendarDate = 1")]
    public async Task AnImmutableColumn_CannotBeChanged(string assignment)
    {
        await using var shop = await GuardedShopAsync();

        var ex = await TryAsync(shop, "UPDATE AuditLogEntries SET " + assignment);

        Assert.NotNull(ex);
        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DroppingTheGuard_AllowsChangesAgain()
    {
        await using var shop = await GuardedShopAsync();
        await ApplyGuardAsync(shop, b => b.DropChronoAuditAuditGuard());

        Assert.Null(await TryAsync(shop, "UPDATE AuditLogEntries SET Action = 'edited'"));
    }

    [Fact]
    public void AnUnsupportedProvider_IsNamedInTheException()
    {
        var builder = new MigrationBuilder("Some.Other.Provider");

        var ex = Assert.Throws<NotSupportedException>(() => builder.CreateChronoAuditAuditGuard());

        Assert.Contains("Some.Other.Provider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedSql_ForEachProvider_IsOneStatementPerTrigger_AndUsesTheDefaultSchema()
    {
        var sqlServer = new MigrationBuilder(ChronoAuditDialect.SqlServerProvider).CreateChronoAuditAuditGuard().Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql).ToList();
        var postgres = new MigrationBuilder(ChronoAuditDialect.PostgreSqlProvider).CreateChronoAuditAuditGuard().Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql).ToList();
        var store = new MigrationBuilder(ChronoAuditDialect.SqlServerProvider).CreateChronoAuditStoreGuards().Operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql).ToList();

        var trigger = Assert.Single(sqlServer);
        Assert.StartsWith("CREATE OR ALTER TRIGGER [dbo].[TR_AuditLogEntries_Guard] ON [dbo].[AuditLogEntries]", trigger, StringComparison.Ordinal);
        Assert.Contains("d.[PayloadHash] IS NOT NULL", trigger, StringComparison.Ordinal);
        Assert.DoesNotContain("[ChainIndex]", trigger.Replace("ChainIndex is", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(3, postgres.Count);
        Assert.Contains("\"public\".\"AuditLogEntries\"", postgres[2], StringComparison.Ordinal);
        Assert.Equal(4, store.Count);
        Assert.Contains(store, s => s.Contains("TR_ApiRequestBodies_Guard", StringComparison.Ordinal) && s.Contains("[chronoaudit]", StringComparison.Ordinal) && s.Contains("append-only.", StringComparison.Ordinal));
    }
}
