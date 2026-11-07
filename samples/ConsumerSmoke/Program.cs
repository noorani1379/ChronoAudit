using System.Runtime.InteropServices;
using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var tfm = RuntimeInformation.FrameworkDescription;
var tfmLabel = tfm.Contains(".NET 8") ? "net8.0" : "net10.0";

try
{
    // 1. Setup SQLite in-memory database
    using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();

    // 2. DI Setup
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IAuditActor>(new SmokeActor("smoke-actor", "Smoke Actor"));
    services.AddSingleton<ICorrelationContext>(new SmokeCorrelation(Guid.NewGuid()));

    services.AddDbContext<SmokeDbContext>(options =>
    {
        options.UseSqlite(connection);
    });

    // 32-byte HMAC key
    var key = new byte[32];
    Array.Fill<byte>(key, 0x42);

    services.AddChronoAudit(key)
        .AddModule<SmokeDbContext>("smoke")
        .UseStore<SmokeDbContext>()
        .AddBuiltInAnalytics();

    using var sp = services.BuildServiceProvider();

    // 3. Create tables
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmokeDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    int productId;

    // 4. Insert
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmokeDbContext>();
        var product = new Product { Id = 1, Name = "Book", Price = 19.99m };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        productId = product.Id;
    }

    // 5. Update
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmokeDbContext>();
        var product = await db.Products.FindAsync(productId);
        if (product == null) throw new InvalidOperationException("Product not found for update");
        product.Name = "Book Deluxe";
        product.Price = 29.99m;
        await db.SaveChangesAsync();
    }

    // 6. Soft Delete
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmokeDbContext>();
        var product = await db.Products.FindAsync(productId);
        if (product == null) throw new InvalidOperationException("Product not found for delete");
        db.Products.Remove(product);
        await db.SaveChangesAsync();
    }

    // 7. Hash
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmokeDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<AuditChain>();
        var hashed = await chain.HashAllPendingAsync(db, 100, CancellationToken.None, "smoke");
        if (hashed < 3) throw new InvalidOperationException($"Expected at least 3 hashed rows, got {hashed}");
    }

    // 8. Verify
    using (var scope = sp.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<SmokeDbContext>();
        var chain = scope.ServiceProvider.GetRequiredService<AuditChain>();
        var verification = await chain.VerifyAsync(db, CancellationToken.None, module: "smoke");
        if (!verification.IsValid) throw new InvalidOperationException($"Hash chain verification failed at sequence {verification.FailedAtSequence}");
    }

    // 9. Read Record History
    using (var scope = sp.CreateScope())
    {
        var reader = scope.ServiceProvider.GetRequiredService<IChangeReader>();
        var history = await reader.RecordAsync("smoke", "Products", productId.ToString(), ChronoAuditScope.Everything(null), null, 10, false, CancellationToken.None);
        if (!history.IsSuccess || history.Value == null || history.Value.Items.Count < 3)
        {
            throw new InvalidOperationException($"Expected at least 3 history items, got {history.Value?.Items.Count ?? 0}");
        }
    }

    // 10. Analytics Query
    using (var scope = sp.CreateScope())
    {
        var analytics = scope.ServiceProvider.GetRequiredService<IAnalyticsService>();
        var query = new AnalyticsQuery(
            Subject: "audit:smoke",
            Dimensions: ["action"],
            Measures: ["count"],
            Filters: null,
            From: DateTimeOffset.UtcNow.AddDays(-1),
            To: DateTimeOffset.UtcNow.AddDays(1),
            SortBy: "action",
            SortDescending: false);

        var result = await analytics.RunAsync(query, ChronoAuditScope.Everything(null), CancellationToken.None);
        if (!result.IsSuccess || result.Value == null || result.Value.Rows.Count == 0)
        {
            throw new InvalidOperationException($"Analytics query failed or returned 0 rows: {result.Error}");
        }
    }

    Console.WriteLine($"SMOKE OK {tfmLabel}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SMOKE FAILED on {tfmLabel}: {ex}");
    return 1;
}

public sealed class Product : AuditableEntityBase
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
}

public sealed class SmokeDbContext(
    DbContextOptions<SmokeDbContext> options,
    IAuditActor actor,
    ICorrelationContext correlation,
    IAuditLogAccessor accessor,
    AuditSignalChannel channel,
    TimeProvider? clock = null)
    : AuditableDbContext(options, actor, correlation, accessor, channel, clock)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyChronoAuditStore(this);
        modelBuilder.Entity<Product>().Property(p => p.Id).ValueGeneratedNever();

        // SQLite cannot compare DateTimeOffset natively (trap T1)
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                }
            }
        }
    }
}

public sealed class SmokeActor(string userId, string displayName) : IAuditActor
{
    public string? UserId => userId;
    public string? DisplayName => displayName;
}

public sealed class SmokeCorrelation(Guid correlationId) : ICorrelationContext
{
    public Guid CorrelationId => correlationId;
}
