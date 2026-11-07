using ChronoAudit.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

/// <summary>A plain (not audited) host context that holds the ChronoAudit store tables.</summary>
public sealed class StoreDb(DbContextOptions<StoreDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyChronoAuditStore(this);
    }
}

/// <summary>A host with an audited module ("shop"), a store context and the activity log, on two SQLite databases.</summary>
public sealed class ActivityHost : IAsyncDisposable
{
    private readonly SqliteConnection _store = new("DataSource=:memory:");
    private readonly SqliteConnection _shop = new("DataSource=:memory:");

    private ActivityHost(Action<ChronoAuditOptions>? configure, byte[]? key, bool withAuditedModule, Action<ChronoAuditBuilder>? customize, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? storeInterceptor)
    {
        _store.Open();
        _shop.Open();
        Clock = new FakeClock(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        services.AddSingleton<IAuditActor>(new FakeAuditActor());
        services.AddSingleton<ICorrelationContext>(new FakeCorrelation(Guid.NewGuid()));
        services.AddSingleton<TimeProvider>(Clock);
#pragma warning disable EF1001
        services.AddDbContext<StoreDb>(o =>
        {
            o.UseSqlite(_store).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>();
            if (storeInterceptor is not null)
            {
                o.AddInterceptors(storeInterceptor);
            }
        });
        services.AddDbContext<ShopDb>(o => o.UseSqlite(_shop).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>());
#pragma warning restore EF1001
        var options = new ChronoAuditOptions();
        configure?.Invoke(options);
        var builder = services.AddChronoAudit(key ?? SqliteShop.Key, options);
        if (withAuditedModule)
        {
            builder.AddModule<ShopDb>("shop");
        }

        builder.UseStore<StoreDb>().AddActivityLog();
        customize?.Invoke(builder);
        Provider = services.BuildServiceProvider();
    }

    public CapturingLoggerProvider Logs { get; } = new();

    public FakeClock Clock { get; }

    public ServiceProvider Provider { get; }

    public static async Task<ActivityHost> CreateAsync(
        Action<ChronoAuditOptions>? configure = null, byte[]? key = null, bool withAuditedModule = true, Action<ChronoAuditBuilder>? customize = null, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? storeInterceptor = null)
    {
        var host = new ActivityHost(configure, key, withAuditedModule, customize, storeInterceptor);
        using var scope = host.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StoreDb>().Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreatedAsync();
        return host;
    }

    public async Task LogAsync(
        int number, string? userId = "u1", string method = "POST", int status = 200, string? sessionId = null, Guid? correlation = null, string? body = null, string? endpoint = null, string? ip = null)
    {
        var id = Guid.Parse("00000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
        var entry = ApiRequestLogEntry.Create(
            id, Clock.GetUtcNow(), TimeSpan.FromMilliseconds(5), method, "/api/items/" + number, status, userId, sessionId: sessionId, correlationId: correlation,
            endpointKey: endpoint ?? "/api/items/{id}", ipAddress: ip, hasBody: body is not null, body: body, bodyContentType: body is null ? null : "application/json");
        await Provider.GetRequiredService<IApiRequestLogStore>().WriteAsync(entry, CancellationToken.None);
    }

    public async Task IntentAsync(int number, string? userId = "u1", DateTimeOffset? startedAt = null)
    {
        var id = Guid.Parse("00000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
        await Provider.GetRequiredService<IApiMutationIntentStore>().WriteAsync(
            new ApiMutationIntent
            {
                RequestId = id,
                StartedAtUtc = startedAt ?? Clock.GetUtcNow(),
                UserId = userId,
                Method = "POST",
                Path = "/api/items",
                EndpointKey = "/api/items",
                BodySha256 = new string('a', 64),
                BodyLength = 10,
            },
            CancellationToken.None);
    }

    public async Task<T> ReadStoreAsync<T>(Func<StoreDb, Task<T>> read)
    {
        using var scope = Provider.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<StoreDb>());
    }

    public async Task<int> HashAsync()
    {
        var worker = new ActivityHashWorker(
            Provider.GetRequiredService<ChronoAuditStoreRegistration>(),
            Provider.GetRequiredService<IServiceScopeFactory>(),
            Provider.GetRequiredService<StoreChain<ApiRequestLog>>(),
            Provider.GetRequiredService<StoreChain<ApiMutationIntent>>(),
            Provider.GetRequiredService<ActivityLogOptions>(),
            Provider.GetRequiredService<ILogger<ActivityHashWorker>>());
        return await worker.RunOnceAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> AuditRowsAsync()
    {
        using var scope = Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDb>().AuditLogEntries.AsNoTracking().OrderBy(a => a.SequenceNumber).ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await _store.DisposeAsync();
        await _shop.DisposeAsync();
    }
}
