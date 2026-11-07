using System.Text;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class BillingDb(
    DbContextOptions<BillingDb> options, IAuditActor actor, ICorrelationContext correlation, IAuditLogAccessor accessor, AuditSignalChannel channel,
    TimeProvider? clock = null)
    : AuditableDbContext(options, actor, correlation, accessor, channel, clock)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Receipt> Receipts => Set<Receipt>();
}

/// <summary>Two audited modules on two SQLite databases, with the services a host registers.</summary>
public sealed class TwoModuleHost : IAsyncDisposable
{
    private readonly SqliteConnection _shop = new("DataSource=:memory:");
    private readonly SqliteConnection _billing = new("DataSource=:memory:");

    private TwoModuleHost(Action<ChronoAuditOptions>? configure)
    {
        _shop.Open();
        _billing.Open();
        Clock = new FakeClock(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditActor>(new FakeAuditActor());
        services.AddSingleton<ICorrelationContext>(new FakeCorrelation(Guid.NewGuid()));
        services.AddSingleton<TimeProvider>(Clock);
#pragma warning disable EF1001
        services.AddDbContext<ShopDb>(o => o.UseSqlite(_shop).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>());
        services.AddDbContext<BillingDb>(o => o.UseSqlite(_billing).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>());
#pragma warning restore EF1001
        var options = new ChronoAuditOptions();
        configure?.Invoke(options);
        services.AddChronoAudit(SqliteShop.Key, options).AddModule<ShopDb>("shop").AddModule<BillingDb>("billing");
        Provider = services.BuildServiceProvider();
    }

    public FakeClock Clock { get; }

    public ServiceProvider Provider { get; }

    public static async Task<TwoModuleHost> CreateAsync(Action<ChronoAuditOptions>? configure = null)
    {
        var host = new TwoModuleHost(configure);
        using var scope = host.Provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<BillingDb>().Database.EnsureCreatedAsync();
        return host;
    }

    public async Task AddAsync(string module, string title, DateTimeOffset? at = null)
    {
        if (at is { } time)
        {
            Clock.Set(time);
        }

        using var scope = Provider.CreateScope();
        if (module == "shop")
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.Orders.Add(new Order { Title = title });
            await db.SaveChangesAsync();
        }
        else
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDb>();
            db.Orders.Add(new Order { Title = title });
            await db.SaveChangesAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await _shop.DisposeAsync();
        await _billing.DisposeAsync();
    }
}

public sealed class ExtractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static ExtractQuery Query() => new(T0.AddHours(-1), T0.AddHours(5));

    private static async Task<ExtractBatch> ReadAllAsync(IExtractReader reader, ExtractQuery query)
    {
        var rows = new List<ExtractRow>();
        while (true)
        {
            var batch = (await reader.NextAsync(query, 10, CancellationToken.None)).Value;
            rows.AddRange(batch.Rows);
            if (batch.Done)
            {
                return new ExtractBatch(rows, null, true);
            }

            query = query with { Cursor = batch.NextCursor };
        }
    }

    private static async Task<(byte[] Bytes, long Rows, string Sha256)> RunAsync(TwoModuleHost host, ExtractQuery query)
    {
        using var stream = new MemoryStream();
        var (rows, sha) = await ExtractCsv.WriteAsync(host.Provider.GetRequiredService<IExtractReader>(), query, stream, CancellationToken.None);
        return (stream.ToArray(), rows, sha);
    }

    [Fact]
    public async Task TheFile_StartsWithTheBom_ThenTheHeader_ThenOneLinePerRow_InOrder()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        await host.AddAsync("shop", "first", T0);
        await host.AddAsync("shop", "second", T0.AddMinutes(1));

        var (bytes, rows, sha) = await RunAsync(host, Query());

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Module,Sequence,OccurredAtUtc,ActorUserId,Action,ResourceType,ResourceId,OldValues,NewValues,Metadata,PayloadHash", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal(2, rows);
        Assert.StartsWith("shop,1,2026-11-01T08:00:00.0000000Z,user-1,Auto_Added,Orders,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("shop,2,", lines[2], StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), sha);
    }

    [Fact]
    public async Task NonAsciiTextZwnjAndSpecialCharacters_AreWrittenAsTheyAre_AndFormulasAreNeutralized()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        var persian = "می‌شود";
        await host.AddAsync("shop", persian + ",\"q\"\nnext", T0);
        await host.AddAsync("shop", "=1+1", T0.AddMinutes(1));

        var (bytes, _, _) = await RunAsync(host, Query());

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains(persian, text, StringComparison.Ordinal);
        Assert.Contains("‌", text, StringComparison.Ordinal);
        // JSON escapes the quote and the line break inside the value; the CSV quotes the whole cell and doubles its quotes.
        Assert.Contains("\"\"", text, StringComparison.Ordinal);
        // The NewValues cell is JSON text starting with '{', so a title that starts with '=' is inside the JSON and safe; a cell that
        // itself starts with '=' (a hand-written resource id) is neutralized by the CSV writer.
        Assert.Equal("'=A1", CsvWriter.Escape("=A1", isText: true));
    }

    [Fact]
    public async Task SecretFields_AreMasked_InTheFile()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            await scope.ServiceProvider.GetRequiredService<IAuditLogWriter>()
                .AppendHashedAsync("user-1", "Login", "Sessions", "1", new { password = "hunter2", user = "ada" });
            db.Orders.Add(new Order { Title = "visible" });
            await db.SaveChangesAsync();
        }

        var (bytes, _, _) = await RunAsync(host, Query());

        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", text, StringComparison.Ordinal);
        Assert.Contains("ada", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredPersonalFields_AreMasked_InTheFile_NeverRevealed()
    {
        await using var host = await TwoModuleHost.CreateAsync(o => o.PersonalData.TextFields.Add("Title"));
        await host.AddAsync("shop", "Ada Lovelace", T0);

        var (bytes, _, _) = await RunAsync(host, Query());

        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("Ada Lovelace", text, StringComparison.Ordinal);
        Assert.Contains("\"\"Title\"\":\"\"***\"\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyBatches_Stream()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        for (var i = 0; i < 7; i++)
        {
            await host.AddAsync("shop", "row " + i, T0.AddSeconds(i));
        }

        var reader = host.Provider.GetRequiredService<IExtractReader>();
        var query = Query();
        var seen = new List<long>();
        var batches = 0;
        while (true)
        {
            var batch = (await reader.NextAsync(query, 3, CancellationToken.None)).Value;
            seen.AddRange(batch.Rows.Select(r => r.Sequence));
            batches++;
            if (batch.Done)
            {
                break;
            }

            query = query with { Cursor = batch.NextCursor };
        }

        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, seen);
        Assert.True(batches >= 3);
    }

    [Fact]
    public async Task Resuming_GivesTheRest_AndRunningTwiceGivesTheSameBytesAndHash()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await host.AddAsync("shop", "row " + i, T0.AddSeconds(i));
        }

        var first = await RunAsync(host, Query());
        var second = await RunAsync(host, Query());
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.Sha256, second.Sha256);

        var reader = host.Provider.GetRequiredService<IExtractReader>();
        var cut = (await reader.NextAsync(Query(), 2, CancellationToken.None)).Value;
        var rest = await RunAsync(host, Query() with { Cursor = cut.NextCursor });
        Assert.Equal(3, rest.Rows);
        Assert.Equal(5, cut.Rows.Count + rest.Rows - 0);
    }

    [Fact]
    public async Task TheCursor_RoundTrips()
    {
        var cursor = new ExtractCursor("billing", 12345);

        var decoded = ExtractCursor.Decode(cursor.Encode());

        Assert.Equal(cursor, decoded);
        Assert.StartsWith("x1.", cursor.Encode(), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shop:5")]
    [InlineData("x1.%%%")]
    [InlineData("x1.c2hvcDo1LTE")]
    [InlineData("x1.OjU")]
    [InlineData("x1.c2hvcDphYmM")]
    public void ATamperedCursor_IsRefused(string? text)
    {
        Assert.Null(ExtractCursor.Decode(text));
    }

    [Fact]
    public async Task ACursorForAnUnknownModuleOrGarbage_IsInvalid()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        var reader = host.Provider.GetRequiredService<IExtractReader>();

        var unknown = await reader.NextAsync(Query() with { Cursor = new ExtractCursor("nope", 1).Encode() }, 10, CancellationToken.None);
        var garbage = await reader.NextAsync(Query() with { Cursor = "garbage" }, 10, CancellationToken.None);

        Assert.Equal("ChronoAudit.InvalidCursor", unknown.Error.Code);
        Assert.Equal("ChronoAudit.InvalidCursor", garbage.Error.Code);
    }

    [Fact]
    public async Task SeveralModules_AreReadModuleByModule()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        await host.AddAsync("shop", "s1", T0);
        await host.AddAsync("billing", "b1", T0.AddSeconds(1));
        await host.AddAsync("shop", "s2", T0.AddSeconds(2));

        var reader = host.Provider.GetRequiredService<IExtractReader>();
        var rows = new List<ExtractRow>();
        var query = Query();
        while (true)
        {
            var batch = (await reader.NextAsync(query, 10, CancellationToken.None)).Value;
            rows.AddRange(batch.Rows);
            if (batch.Done)
            {
                break;
            }

            query = query with { Cursor = batch.NextCursor };
        }

        // Modules in ordinal order, inside a module by sequence.
        Assert.Equal(new[] { "billing", "shop", "shop" }, rows.Select(r => r.Module));
        Assert.Equal(new long[] { 1, 1, 2 }, rows.Select(r => r.Sequence));
    }

    [Fact]
    public async Task ChosenTables_Only()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        await host.AddAsync("shop", "s1", T0);
        await host.AddAsync("billing", "b1", T0.AddSeconds(1));
        using (var scope = host.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
            db.Payments.Add(new Payment { Amount = 5m });
            await db.SaveChangesAsync();
        }

        var reader = host.Provider.GetRequiredService<IExtractReader>();
        var onlyOrders = (await reader.NextAsync(Query() with { Tables = ["shop/Orders"] }, 10, CancellationToken.None)).Value;
        Assert.Equal(new[] { "shop" }, onlyOrders.Rows.Select(r => r.Module).Distinct());
        Assert.All(onlyOrders.Rows, r => Assert.Equal("Orders", r.ResourceType));

        var payments = (await reader.NextAsync(Query() with { Tables = ["shop/Payments"] }, 10, CancellationToken.None)).Value;
        Assert.Single(payments.Rows);

        var byType = (await reader.NextAsync(Query() with { ResourceType = "Payments" }, 10, CancellationToken.None)).Value;
        Assert.Single(byType.Rows);
        var byActor = (await reader.NextAsync(Query() with { ActorUserId = "nobody" }, 10, CancellationToken.None)).Value;
        Assert.Empty(byActor.Rows);
    }

    [Fact]
    public async Task WithoutTheFilter_Unchanged()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        await host.AddAsync("shop", "s1", T0);
        await host.AddAsync("billing", "b1", T0.AddSeconds(1));
        var reader = host.Provider.GetRequiredService<IExtractReader>();

        var all = await ReadAllAsync(reader, Query());
        var modulesOnly = await ReadAllAsync(reader, Query() with { Modules = ["billing"] });

        Assert.Equal(2, all.Rows.Count);
        Assert.Equal(new[] { "billing" }, modulesOnly.Rows.Select(r => r.Module));
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("shop/")]
    [InlineData("/Orders")]
    [InlineData("shop/Or ders")]
    [InlineData("a/b/c")]
    public async Task AMalformedTable_IsRefused(string table)
    {
        await using var host = await TwoModuleHost.CreateAsync();
        var reader = host.Provider.GetRequiredService<IExtractReader>();

        var result = await reader.NextAsync(Query() with { Tables = [table] }, 10, CancellationToken.None);

        Assert.Equal("Extract.InvalidTable", result.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task TheQuery_IsValidated()
    {
        await using var host = await TwoModuleHost.CreateAsync();
        var reader = host.Provider.GetRequiredService<IExtractReader>();

        Assert.Equal("ChronoAudit.Validation", (await reader.NextAsync(Query() with { To = T0.AddHours(-5) }, 10, CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await reader.NextAsync(new ExtractQuery(T0, T0.AddDays(367)), 10, CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await reader.NextAsync(Query(), 0, CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await reader.NextAsync(Query(), 5001, CancellationToken.None)).Error.Code);
        Assert.Equal("ChronoAudit.Validation", (await reader.NextAsync(Query() with { Modules = ["nope"] }, 10, CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task TheColumns_AreFixed()
    {
        await using var host = await TwoModuleHost.CreateAsync();

        var columns = host.Provider.GetRequiredService<IExtractReader>().Columns;

        Assert.Equal(
            new[] { "Module", "Sequence", "OccurredAtUtc", "ActorUserId", "Action", "ResourceType", "ResourceId", "OldValues", "NewValues", "Metadata", "PayloadHash" },
            columns.Select(c => c.Header));
        Assert.False(columns.Single(c => c.Header == "Sequence").IsText);
        Assert.False(columns.Single(c => c.Header == "OccurredAtUtc").IsText);
    }
}
