using System.Data.Common;
using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.IntegrationTests;

public sealed class TestDatabase : IAsyncDisposable
{
    public static readonly byte[] Key = Encoding.UTF8.GetBytes("integration-test-key-0123456789-0123456789");

    // CREATE and DROP DATABASE share a small throw-away server with the other test processes: give them time, not a verdict.
    private const int SetupTimeoutSeconds = 180;

    private readonly string _masterConn;

    private TestDatabase(
        DatabaseProvider provider,
        string databaseName,
        string masterConn,
        string targetConn,
        ServiceProvider services,
        FakeClock clock,
        FakeAuditActor actor,
        FakeCorrelation correlation)
    {
        Provider = provider;
        DatabaseName = databaseName;
        _masterConn = masterConn;
        ConnectionString = targetConn;
        Services = services;
        Clock = clock;
        Actor = actor;
        Correlation = correlation;
    }

    public DatabaseProvider Provider { get; }

    public string DatabaseName { get; }

    public string ConnectionString { get; }

    public ServiceProvider Services { get; }

    public FakeClock Clock { get; }

    public FakeAuditActor Actor { get; }

    public FakeCorrelation Correlation { get; }

    public IServiceScope Scope() => Services.CreateScope();

    public IntegrationShopDb Shop(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IntegrationShopDb>();

    public IntegrationStoreDb Store(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IntegrationStoreDb>();

    public static async Task<TestDatabase> CreateAsync(
        DatabaseProvider provider,
        Action<ChronoAuditOptions>? configureOptions = null,
        DateTimeOffset? now = null,
        Action<ChronoAuditBuilder>? customize = null)
    {
        var dbName = "chronoaudit_test_" + Guid.NewGuid().ToString("N");
        string masterConn;
        string targetConn;

        if (provider == DatabaseProvider.SqlServer)
        {
            var raw = Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_SQLSERVER")
                ?? Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_SQLSERVER", EnvironmentVariableTarget.User)
                ?? throw new InvalidOperationException("CHRONOAUDIT_TEST_SQLSERVER is not set.");
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(raw);
            builder.InitialCatalog = "master";
            masterConn = builder.ConnectionString;
            builder.InitialCatalog = dbName;
            targetConn = builder.ConnectionString;

            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(masterConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{dbName}]; ALTER DATABASE [{dbName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;";
            cmd.CommandTimeout = SetupTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync();
        }
        else
        {
            var raw = Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_POSTGRES")
                ?? Environment.GetEnvironmentVariable("CHRONOAUDIT_TEST_POSTGRES", EnvironmentVariableTarget.User)
                ?? throw new InvalidOperationException("CHRONOAUDIT_TEST_POSTGRES is not set.");
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(raw);
            builder.Database = "postgres";
            masterConn = builder.ConnectionString;
            builder.Database = dbName;
            targetConn = builder.ConnectionString;

            await using var conn = new Npgsql.NpgsqlConnection(masterConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\";";
            cmd.CommandTimeout = SetupTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        var clock = new FakeClock(now ?? new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero));
        var actor = new FakeAuditActor();
        var correlation = new FakeCorrelation(Guid.NewGuid());
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IAuditActor>(actor);
        services.AddSingleton<ICorrelationContext>(correlation);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));

        if (provider == DatabaseProvider.SqlServer)
        {
            services.AddDbContext<IntegrationShopDb>(o => o.UseSqlServer(targetConn));
            services.AddDbContext<IntegrationStoreDb>(o => o.UseSqlServer(targetConn));
        }
        else
        {
            services.AddDbContext<IntegrationShopDb>(o => o.UseNpgsql(targetConn));
            services.AddDbContext<IntegrationStoreDb>(o => o.UseNpgsql(targetConn));
        }

        var chronoOptions = new ChronoAuditOptions();
        configureOptions?.Invoke(chronoOptions);

        var chronoBuilder = services.AddChronoAudit(Key, chronoOptions)
            .AddModule<IntegrationShopDb>("shop")
            .UseStore<IntegrationStoreDb>()
            .AddActivityLog()
            .AddSavedReports()
            .AddExports()
            .AddTimeline()
            .AddBuiltInAnalytics();

        customize?.Invoke(chronoBuilder);

        var sp = services.BuildServiceProvider();

        // Build schemas, tables, and guard triggers
        using (var scope = sp.CreateScope())
        {
            var shopDb = scope.ServiceProvider.GetRequiredService<IntegrationShopDb>();
            var storeDb = scope.ServiceProvider.GetRequiredService<IntegrationStoreDb>();
            shopDb.Database.SetCommandTimeout(SetupTimeoutSeconds);
            storeDb.Database.SetCommandTimeout(SetupTimeoutSeconds);

            if (provider == DatabaseProvider.SqlServer)
            {
                await shopDb.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'shop') EXEC('CREATE SCHEMA shop'); " +
                    "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'chronoaudit') EXEC('CREATE SCHEMA chronoaudit');");
            }
            else
            {
                await shopDb.Database.ExecuteSqlRawAsync(
                    "CREATE SCHEMA IF NOT EXISTS shop; CREATE SCHEMA IF NOT EXISTS chronoaudit;");
            }

            var shopCreator = (IRelationalDatabaseCreator)shopDb.Database.GetService<IDatabaseCreator>();
            await shopCreator.CreateTablesAsync();

            var storeCreator = (IRelationalDatabaseCreator)storeDb.Database.GetService<IDatabaseCreator>();
            await storeCreator.CreateTablesAsync();

            // Guard triggers
            var shopMigrationBuilder = new MigrationBuilder(shopDb.Database.ProviderName);
            shopMigrationBuilder.CreateChronoAuditAuditGuard("shop");
            var shopSqlGen = shopDb.Database.GetService<IMigrationsSqlGenerator>();
            var shopCommands = shopSqlGen.Generate(shopMigrationBuilder.Operations, shopDb.Model);
            foreach (var cmd in shopCommands)
            {
                await shopDb.Database.ExecuteSqlRawAsync(cmd.CommandText);
            }

            var storeMigrationBuilder = new MigrationBuilder(storeDb.Database.ProviderName);
            storeMigrationBuilder.CreateChronoAuditStoreGuards("chronoaudit");
            var storeSqlGen = storeDb.Database.GetService<IMigrationsSqlGenerator>();
            var storeCommands = storeSqlGen.Generate(storeMigrationBuilder.Operations, storeDb.Model);
            foreach (var cmd in storeCommands)
            {
                await storeDb.Database.ExecuteSqlRawAsync(cmd.CommandText);
            }
        }

        return new TestDatabase(provider, dbName, masterConn, targetConn, sp, clock, actor, correlation);
    }

    public async Task<DbConnection> CreateOpenConnectionAsync()
    {
        DbConnection conn = Provider == DatabaseProvider.SqlServer
            ? new Microsoft.Data.SqlClient.SqlConnection(ConnectionString)
            : new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<int> ExecuteRawAsync(string sql)
    {
        using var scope = Scope();
        var shopDb = Shop(scope);
        return await shopDb.Database.ExecuteSqlRawAsync(sql);
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql)
    {
        await using var conn = await CreateOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var res = await cmd.ExecuteScalarAsync();
        if (res is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(res, typeof(T));
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();

        try
        {
            if (Provider == DatabaseProvider.SqlServer)
            {
                Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
                await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_masterConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];";
                cmd.CommandTimeout = SetupTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                Npgsql.NpgsqlConnection.ClearAllPools();
                await using var conn = new Npgsql.NpgsqlConnection(_masterConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP DATABASE \"{DatabaseName}\" WITH (FORCE);";
                cmd.CommandTimeout = SetupTimeoutSeconds;
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch
        {
            // Best effort cleanup in test disposal
        }
    }
}

public abstract class DatabaseFixture(DatabaseProvider provider) : IAsyncLifetime
{
    public DatabaseProvider Provider { get; } = provider;

    public TestDatabase Database { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        var envVar = Provider == DatabaseProvider.SqlServer ? "CHRONOAUDIT_TEST_SQLSERVER" : "CHRONOAUDIT_TEST_POSTGRES";
        var val = Environment.GetEnvironmentVariable(envVar)
            ?? Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(val))
        {
            return;
        }

        Database = await TestDatabase.CreateAsync(Provider);
    }

    public virtual async Task DisposeAsync()
    {
        if (Database is not null)
        {
            await Database.DisposeAsync();
        }
    }
}

public sealed class SqlServerDatabaseFixture() : DatabaseFixture(DatabaseProvider.SqlServer);

public sealed class PostgreSqlDatabaseFixture() : DatabaseFixture(DatabaseProvider.PostgreSql);
