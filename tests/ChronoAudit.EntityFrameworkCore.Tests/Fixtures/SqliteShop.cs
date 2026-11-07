using ChronoAudit.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>
/// SQLite cannot order or compare a DateTimeOffset column natively, so the test model stores it as a binary number (trap T1).
/// Library code never adds this converter.
/// </summary>
#pragma warning disable EF1001 // ModelCustomizer is the documented base class for replacing the model customizer in tests.
public sealed class SqliteTestModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
#pragma warning restore EF1001
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
                }
            }
        }
    }
}

/// <summary>One open in-memory SQLite database plus the services a host would register.</summary>
public sealed class SqliteShop : IAsyncDisposable
{
    public static readonly byte[] Key = System.Text.Encoding.UTF8.GetBytes("unit-test-hmac-key-0123456789-0123456789");

    private readonly SqliteConnection? _connection;
    private readonly string? _filePath;

    public SqliteShop(Action<ChronoAuditOptions>? configure = null, DateTimeOffset? now = null, IChronoAuditCalendar? calendar = null, byte[]? key = null, Action<ChronoAuditBuilder>? customize = null, bool fileBacked = false)
    {
        // The default is ONE shared in-memory connection (not thread-safe). A test with a real background worker needs a file, where every context has its own connection.
        _filePath = fileBacked ? Path.Combine(Path.GetTempPath(), "chronoaudit-shop-" + Guid.NewGuid().ToString("N") + ".db") : null;
        _connection = fileBacked ? null : new SqliteConnection("DataSource=:memory:");
        _connection?.Open();

        Actor = new FakeAuditActor();
        Correlation = new FakeCorrelation(Guid.NewGuid());
        Clock = new FakeClock(now ?? new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        services.AddSingleton<IAuditActor>(Actor);
        services.AddSingleton<ICorrelationContext>(Correlation);
        services.AddSingleton<TimeProvider>(Clock);
        if (calendar is not null)
        {
            services.AddSingleton(calendar);
        }

        services.AddDbContext<ShopDb>(o =>
        {
            if (_connection is not null)
            {
                o.UseSqlite(_connection);
            }
            else
            {
                o.UseSqlite("DataSource=" + _filePath);
            }

            o.ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>();
        });
        var options = new ChronoAuditOptions();
        configure?.Invoke(options);
        var builder = services.AddChronoAudit(key ?? Key, options).AddModule<ShopDb>("shop");
        customize?.Invoke(builder);
        Provider = services.BuildServiceProvider();
    }

    public CapturingLoggerProvider Logs { get; } = new();

    public FakeAuditActor Actor { get; }

    public FakeCorrelation Correlation { get; }

    public FakeClock Clock { get; }

    public ServiceProvider Provider { get; }

    public static async Task<SqliteShop> CreateAsync(
        Action<ChronoAuditOptions>? configure = null, DateTimeOffset? now = null, IChronoAuditCalendar? calendar = null, byte[]? key = null, Action<ChronoAuditBuilder>? customize = null, bool fileBacked = false)
    {
        var shop = new SqliteShop(configure, now, calendar, key, customize, fileBacked);
        using var scope = shop.Scope();
        await Get<ShopDb>(scope).Database.EnsureCreatedAsync();
        return shop;
    }

    public IServiceScope Scope() => Provider.CreateScope();

    public static T Get<T>(IServiceScope scope)
        where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>The persisted audit rows in chain order, read back with a new context.</summary>
    public async Task<List<AuditLogEntry>> AuditRowsAsync()
    {
        using var scope = Scope();
        return await Get<ShopDb>(scope).AuditLogEntries.AsNoTracking().OrderBy(a => a.SequenceNumber).ToListAsync();
    }

    /// <summary>Adds audit rows without the audit pipeline, to test the guards on rows that already exist.</summary>
    public async Task SeedAuditRowAsync(string table, string key, string action)
    {
        using var scope = Scope();
        var db = Get<ShopDb>(scope);
        db.BypassAuditingForSeed = true;
        db.AuditLogEntries.Add(new AuditLogEntry
        {
            CorrelationId = Guid.NewGuid(),
            ActorUserId = "user-1",
            Action = action,
            ResourceType = table,
            ResourceId = key,
            NewValues = "{}",
        });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_filePath is not null)
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { _filePath, _filePath + "-wal", _filePath + "-shm", _filePath + "-journal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
