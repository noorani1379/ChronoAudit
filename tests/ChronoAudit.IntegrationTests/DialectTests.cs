using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SqlServerDialectTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public async Task DuplicateKey_IsRecognizedAsUniqueViolation_OnSqlServer()
    {
        var db = fixture.Database;
        var dialect = ChronoAuditDialect.For(ChronoAuditDialect.SqlServerProvider);

        var id = Guid.NewGuid();
        var row1 = new AuditLogEntry
        {
            Id = id,
            CorrelationId = Guid.NewGuid(),
            ActorUserId = "u1",
            Action = "test",
            ResourceType = "res",
            ResourceId = "1",
        };
        var row2 = new AuditLogEntry
        {
            Id = id,
            CorrelationId = Guid.NewGuid(),
            ActorUserId = "u2",
            Action = "test",
            ResourceType = "res",
            ResourceId = "2",
        };

        using var scope = db.Scope();
        var shop = db.Shop(scope);
        shop.AuditLogEntries.Add(row1);
        await shop.SaveChangesAsync();

        shop.AuditLogEntries.Add(row2);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => shop.SaveChangesAsync());

        Assert.True(dialect.IsUniqueViolation(ex), $"Expected unique violation, got: {ex}");
    }

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public async Task Deadlock_IsRecognizedAsTransient_OnSqlServer()
    {
        var db = fixture.Database;
        var dialect = ChronoAuditDialect.For(ChronoAuditDialect.SqlServerProvider);

        // Seed two orders
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            shop.Orders.Add(new Order { Id = id1, Title = "Order1", Quantity = 1 });
            shop.Orders.Add(new Order { Id = id2, Title = "Order2", Quantity = 2 });
            await shop.SaveChangesAsync();
        }

        await using var conn1 = await db.CreateOpenConnectionAsync();
        await using var conn2 = await db.CreateOpenConnectionAsync();

        await using var tx1 = await conn1.BeginTransactionAsync();
        await using var tx2 = await conn2.BeginTransactionAsync();

        // Conn 1 updates row 1
        await using (var cmd = conn1.CreateCommand())
        {
            cmd.Transaction = tx1;
            cmd.CommandText = $"UPDATE shop.Orders SET Title = 'Updated1' WHERE Id = '{id1}'";
            await cmd.ExecuteNonQueryAsync();
        }

        // Conn 2 updates row 2
        await using (var cmd = conn2.CreateCommand())
        {
            cmd.Transaction = tx2;
            cmd.CommandText = $"UPDATE shop.Orders SET Title = 'Updated2' WHERE Id = '{id2}'";
            await cmd.ExecuteNonQueryAsync();
        }

        // Conn 1 attempts to update row 2 (will block waiting for Conn 2)
        var t1 = Task.Run(async () =>
        {
            await using var cmd = conn1.CreateCommand();
            cmd.Transaction = tx1;
            cmd.CommandText = $"UPDATE shop.Orders SET Title = 'Deadlock1' WHERE Id = '{id2}'";
            await cmd.ExecuteNonQueryAsync();
        });

        await Task.Delay(200);

        // Conn 2 attempts to update row 1 (causes deadlock)
        var t2 = Task.Run(async () =>
        {
            await using var cmd = conn2.CreateCommand();
            cmd.Transaction = tx2;
            cmd.CommandText = $"UPDATE shop.Orders SET Title = 'Deadlock2' WHERE Id = '{id1}'";
            await cmd.ExecuteNonQueryAsync();
        });

        var tasks = new[] { t1, t2 };
        Exception? caught = null;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.True(dialect.IsTransient(caught), $"Expected deadlock to be classified as transient, but was: {caught}");
    }
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlDialectTests(PostgreSqlDatabaseFixture fixture) : IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public async Task DuplicateKey_IsRecognizedAsUniqueViolation_OnPostgreSql()
    {
        var db = fixture.Database;
        var dialect = ChronoAuditDialect.For(ChronoAuditDialect.PostgreSqlProvider);

        var id = Guid.NewGuid();
        var row1 = new AuditLogEntry
        {
            Id = id,
            CorrelationId = Guid.NewGuid(),
            ActorUserId = "u1",
            Action = "test",
            ResourceType = "res",
            ResourceId = "1",
        };
        var row2 = new AuditLogEntry
        {
            Id = id,
            CorrelationId = Guid.NewGuid(),
            ActorUserId = "u2",
            Action = "test",
            ResourceType = "res",
            ResourceId = "2",
        };

        using var scope = db.Scope();
        var shop = db.Shop(scope);
        shop.AuditLogEntries.Add(row1);
        await shop.SaveChangesAsync();

        shop.AuditLogEntries.Add(row2);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => shop.SaveChangesAsync());

        Assert.True(dialect.IsUniqueViolation(ex), $"Expected unique violation, got: {ex}");
    }

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public async Task SerializationFailure_IsRecognizedAsTransient_OnPostgreSql()
    {
        var db = fixture.Database;
        var dialect = ChronoAuditDialect.For(ChronoAuditDialect.PostgreSqlProvider);

        using (var scope = db.Scope())
        {
            var shop = db.Shop(scope);
            shop.Receipts.Add(new Receipt { Text = "X" });
            shop.Receipts.Add(new Receipt { Text = "Y" });
            await shop.SaveChangesAsync();
        }

        await using var conn1 = await db.CreateOpenConnectionAsync();
        await using var conn2 = await db.CreateOpenConnectionAsync();

        await using (var cmd = conn1.CreateCommand())
        {
            cmd.CommandText = "BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn2.CreateCommand())
        {
            cmd.CommandText = "BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;";
            await cmd.ExecuteNonQueryAsync();
        }

        // C1 reads X
        await using (var cmd = conn1.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM shop.\"Receipts\" WHERE \"Text\" = 'X';";
            await cmd.ExecuteScalarAsync();
        }

        // C2 reads Y
        await using (var cmd = conn2.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM shop.\"Receipts\" WHERE \"Text\" = 'Y';";
            await cmd.ExecuteScalarAsync();
        }

        // C1 inserts Y
        await using (var cmd = conn1.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO shop.\"Receipts\" (\"Id\", \"Text\") VALUES ('{Guid.NewGuid()}', 'Y');";
            await cmd.ExecuteNonQueryAsync();
        }

        // C2 inserts X
        await using (var cmd = conn2.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO shop.\"Receipts\" (\"Id\", \"Text\") VALUES ('{Guid.NewGuid()}', 'X');";
            await cmd.ExecuteNonQueryAsync();
        }

        // C1 commits successfully
        await using (var cmd = conn1.CreateCommand())
        {
            cmd.CommandText = "COMMIT;";
            await cmd.ExecuteNonQueryAsync();
        }

        // C2 commit fails with serialization failure 40001
        Exception? caught = null;
        try
        {
            await using var cmd = conn2.CreateCommand();
            cmd.CommandText = "COMMIT;";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.True(dialect.IsTransient(caught), $"Expected serialization failure to be transient, got: {caught}");
    }
}
