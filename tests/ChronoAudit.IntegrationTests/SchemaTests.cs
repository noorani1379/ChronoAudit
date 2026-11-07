using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ChronoAudit.IntegrationTests;

public abstract class SchemaTestsBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected async Task VerifyAuditAndStoreTablesExistAsync()
    {
        var db = Fixture.Database;
        var tables = new[]
        {
            ("shop", "AuditLogEntries"),
            ("shop", "Orders"),
            ("chronoaudit", "ApiRequestLogs"),
            ("chronoaudit", "ApiRequestBodies"),
            ("chronoaudit", "ApiMutationIntents"),
            ("chronoaudit", "SavedReports"),
            ("chronoaudit", "ExportJobs"),
            ("chronoaudit", "TimelineEvents"),
            ("chronoaudit", "TimelineParticipants"),
        };

        foreach (var (schema, table) in tables)
        {
            if (db.Provider == DatabaseProvider.SqlServer)
            {
                var sql = $"SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{schema}' AND t.name = '{table}'";
                var count = await db.ExecuteScalarAsync<int>(sql);
                Assert.True(count > 0, $"Table {schema}.{table} should exist on SQL Server.");
            }
            else
            {
                var sql = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{schema}' AND LOWER(table_name) = '{table.ToLowerInvariant()}'";
                var count = await db.ExecuteScalarAsync<long>(sql);
                Assert.True(count > 0, $"Table {schema}.{table} should exist on PostgreSQL.");
            }
        }
    }

    protected async Task VerifyUnhashedIndexesAreFilteredAsync()
    {
        var db = Fixture.Database;
        var indexNames = new[]
        {
            "IX_AuditLogEntries_Unhashed",
            "IX_ApiRequestLogs_Unhashed",
            "IX_ApiMutationIntents_Unhashed"
        };

        foreach (var indexName in indexNames)
        {
            if (db.Provider == DatabaseProvider.SqlServer)
            {
                var sql = $"SELECT has_filter FROM sys.indexes WHERE name = '{indexName}'";
                var hasFilter = await db.ExecuteScalarAsync<bool>(sql);
                Assert.True(hasFilter, $"Index {indexName} should be filtered on SQL Server.");
            }
            else
            {
                var sql = $"SELECT indexdef FROM pg_indexes WHERE LOWER(indexname) = '{indexName.ToLowerInvariant()}'";
                var def = await db.ExecuteScalarAsync<string>(sql);
                Assert.NotNull(def);
                Assert.Contains("WHERE", def, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    protected async Task VerifyAuditKeyIsIdentityAsync()
    {
        var db = Fixture.Database;
        if (db.Provider == DatabaseProvider.SqlServer)
        {
            var sql = "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID('shop.AuditLogEntries') AND name = 'SequenceNumber'";
            var count = await db.ExecuteScalarAsync<int>(sql);
            Assert.Equal(1, count);
        }
        else
        {
            var sql = "SELECT is_identity, column_default FROM information_schema.columns WHERE table_schema = 'shop' AND LOWER(table_name) = 'auditlogentries' AND LOWER(column_name) = 'sequencenumber'";
            await using var conn = await db.CreateOpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "SequenceNumber column should exist on PostgreSQL");
            var isIdentity = reader.GetString(0);
            var colDefault = reader.IsDBNull(1) ? "" : reader.GetString(1);
            Assert.True(isIdentity == "YES" || colDefault.Contains("nextval", StringComparison.OrdinalIgnoreCase),
                $"Audit sequence key should be identity or sequence generated on PostgreSQL (is_identity: {isIdentity}, default: {colDefault})");
        }
    }

#if NET10_0_OR_GREATER
    // The sample context for a provider, with the migrations of that provider only.
    private static DbContextOptions<ChronoAudit.Sample.WebApi.LibraryDbContext> SampleOptions(DatabaseProvider provider, string connection)
    {
        var builder = new DbContextOptionsBuilder<ChronoAudit.Sample.WebApi.LibraryDbContext>();
        if (provider == DatabaseProvider.SqlServer)
        {
            builder.UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "library"));
        }
        else
        {
            builder.UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "library"));
        }

        builder.ReplaceService<IMigrationsAssembly, ChronoAudit.Sample.WebApi.ProviderMigrationsAssembly>();
        return builder.Options;
    }
#endif

    protected Task VerifySampleMigrationsMatchTheModelAsync()
    {
#if NET10_0_OR_GREATER
        var db = Fixture.Database;
        var dummy = db.Provider == DatabaseProvider.SqlServer
            ? "Server=localhost;Database=dummy;Trusted_Connection=True;TrustServerCertificate=True"
            : "Host=localhost;Database=dummy;Username=postgres";

        using var context = new ChronoAudit.Sample.WebApi.LibraryDbContext(SampleOptions(db.Provider, dummy));
        var hasPendingChanges = context.Database.HasPendingModelChanges();
        Assert.False(hasPendingChanges, $"Sample migrations for {db.Provider} should match the model without pending changes.");
#endif
        return Task.CompletedTask;
    }

    // The model comparison cannot see the SQL of a migration. So the sample's migration is applied to an empty database, the way
    // `dotnet ef database update` does it, and then the sample context saves an entity with a key the database generates.
    protected async Task VerifyTheSampleMigrationAppliesToAnEmptyDatabaseAsync()
    {
#if NET10_0_OR_GREATER
        var db = Fixture.Database;
        var name = "chronoaudit_sample_" + Guid.NewGuid().ToString("N");
        var isSqlServer = db.Provider == DatabaseProvider.SqlServer;

        string master;
        string target;
        if (isSqlServer)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "master" };
            master = builder.ConnectionString;
            builder.InitialCatalog = name;
            target = builder.ConnectionString;
        }
        else
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = "postgres" };
            master = builder.ConnectionString;
            builder.Database = name;
            target = builder.ConnectionString;
        }

        async Task RunOnMasterAsync(string sql)
        {
            await using System.Data.Common.DbConnection conn = isSqlServer
                ? new Microsoft.Data.SqlClient.SqlConnection(master)
                : new Npgsql.NpgsqlConnection(master);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 180;
            await cmd.ExecuteNonQueryAsync();
        }

        await RunOnMasterAsync(isSqlServer ? $"CREATE DATABASE [{name}]" : $"CREATE DATABASE \"{name}\"");
        try
        {
            using (var context = new ChronoAudit.Sample.WebApi.LibraryDbContext(SampleOptions(db.Provider, target)))
            {
                await context.Database.MigrateAsync();

                // Only the migration of this provider ran.
                var applied = Assert.Single(await context.Database.GetAppliedMigrationsAsync());
                Assert.EndsWith("_Initial", applied, StringComparison.Ordinal);

                var triggers = await context.Database.SqlQueryRaw<int>(isSqlServer
                    ? "SELECT COUNT(*) AS \"Value\" FROM sys.triggers WHERE name LIKE 'TR[_]%[_]Guard'"
                    : "SELECT COUNT(*)::int AS \"Value\" FROM pg_trigger WHERE NOT tgisinternal AND tgname LIKE 'TR%Guard'").SingleAsync();
                Assert.Equal(5, triggers);

                // An entity with a generated key is audited with its real key, through the sample's own context.
                var book = new ChronoAudit.Sample.WebApi.Book { Title = "Dune", Isbn = "9780441172719", Author = "Frank Herbert" };
                context.Books.Add(book);
                await context.SaveChangesAsync();
                Assert.True(book.Id > 0);

                var row = await context.AuditLogEntries.AsNoTracking().SingleAsync(a => a.ResourceType == "Books");
                Assert.Contains($"\"Id\":{book.Id}", row.ResourceId, StringComparison.Ordinal);
            }

            if (isSqlServer)
            {
                Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            }
            else
            {
                Npgsql.NpgsqlConnection.ClearAllPools();
            }
        }
        finally
        {
            await RunOnMasterAsync(isSqlServer
                ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];"
                : $"DROP DATABASE \"{name}\" WITH (FORCE);");
        }
#else
        await Task.CompletedTask;
#endif
    }

    // The guard triggers are SQL, not model, so the model comparison above cannot see a migration that forgot them.
    protected Task VerifyTheSampleMigrationCreatesAndDropsTheGuardTriggersAsync()
    {
#if NET10_0_OR_GREATER
        var db = Fixture.Database;
        Migration migration = db.Provider == DatabaseProvider.SqlServer
            ? new ChronoAudit.Sample.WebApi.Migrations.SqlServer.Initial { ActiveProvider = "Microsoft.EntityFrameworkCore.SqlServer" }
            : new ChronoAudit.Sample.WebApi.Migrations.PostgreSql.Initial { ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL" };

        static string[] SqlOf(IReadOnlyList<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> operations)
            => operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(o => o.Sql).ToArray();

        var up = string.Join('\n', SqlOf(migration.UpOperations));
        var down = SqlOf(migration.DownOperations);

        foreach (var table in new[] { "AuditLogEntries", "ApiRequestLogs", "ApiMutationIntents", "ApiRequestBodies", "TimelineEvents" })
        {
            Assert.Contains($"TR_{table}_Guard", up, StringComparison.Ordinal);
            Assert.Contains(down, sql => sql.Contains($"TR_{table}_Guard", StringComparison.Ordinal));
        }

        // The guards must be dropped before the tables they sit on.
        var firstDropTable = migration.DownOperations.ToList().FindIndex(o => o is Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation);
        var lastDropTrigger = migration.DownOperations.ToList().FindLastIndex(o => o is Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation);
        Assert.True(lastDropTrigger < firstDropTable, "The Down method must drop the guard triggers before it drops any table.");
#endif
        return Task.CompletedTask;
    }

}

[Trait("Category", "Integration")]
public sealed class SqlServerSchemaTests(SqlServerDatabaseFixture fixture)
    : SchemaTestsBase(fixture), IClassFixture<SqlServerDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task TablesExistInSchemas() => VerifyAuditAndStoreTablesExistAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task UnhashedIndexesAreFiltered() => VerifyUnhashedIndexesAreFilteredAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task AuditKeyIsIdentity() => VerifyAuditKeyIsIdentityAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task TheSampleMigrations_MatchTheModel() => VerifySampleMigrationsMatchTheModelAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task TheSampleMigration_CreatesAndDropsTheGuardTriggers() => VerifyTheSampleMigrationCreatesAndDropsTheGuardTriggersAsync();

    [DatabaseFact(DatabaseProvider.SqlServer)]
    public Task TheSampleMigration_AppliesToAnEmptyDatabase() => VerifyTheSampleMigrationAppliesToAnEmptyDatabaseAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlSchemaTests(PostgreSqlDatabaseFixture fixture)
    : SchemaTestsBase(fixture), IClassFixture<PostgreSqlDatabaseFixture>
{
    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task TablesExistInSchemas() => VerifyAuditAndStoreTablesExistAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task UnhashedIndexesAreFiltered() => VerifyUnhashedIndexesAreFilteredAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task AuditKeyIsIdentity() => VerifyAuditKeyIsIdentityAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task TheSampleMigrations_MatchTheModel() => VerifySampleMigrationsMatchTheModelAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task TheSampleMigration_CreatesAndDropsTheGuardTriggers() => VerifyTheSampleMigrationCreatesAndDropsTheGuardTriggersAsync();

    [DatabaseFact(DatabaseProvider.PostgreSql)]
    public Task TheSampleMigration_AppliesToAnEmptyDatabase() => VerifyTheSampleMigrationAppliesToAnEmptyDatabaseAsync();


}
