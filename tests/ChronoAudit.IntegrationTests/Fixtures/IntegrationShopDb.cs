using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.IntegrationTests;

public sealed class Order : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "";

    public int Quantity { get; set; }

    [SensitiveData]
    public string Secret { get; set; } = "";
}

public sealed class OrderLine
{
    public Guid OrderId { get; set; }

    public int LineNo { get; set; }

    public string Sku { get; set; } = "";
}

public sealed class Receipt : IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = "";
}

public sealed class Noise : IExcludedFromAutoAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Number { get; set; }
}

[AuditActorNameAsRole]
public sealed class Payment : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public decimal Amount { get; set; }
}

public sealed class CustomerProfile : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string PhoneNumber { get; set; } = "";

    public string FullName { get; set; } = "";

    public string City { get; set; } = "";
}

/// <summary>An entity whose key the database makes (identity on SQL Server, a sequence on PostgreSQL).</summary>
public sealed class Ticket : AuditableEntityBase
{
    public int Id { get; set; }

    public string Title { get; set; } = "";
}

public sealed class IntegrationShopDb : AuditableDbContext
{
    private readonly AuditTrailOptions _auditOptions;

    public IntegrationShopDb(
        DbContextOptions<IntegrationShopDb> options,
        IAuditActor actor,
        ICorrelationContext correlation,
        IAuditLogAccessor accessor,
        AuditSignalChannel channel,
        AuditTrailOptions auditOptions,
        TimeProvider? clock = null,
        IChronoAuditCalendar? calendar = null)
        : base(options, actor, correlation, accessor, channel, clock, calendar)
    {
        _auditOptions = auditOptions;
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<Noise> Noises => Set<Noise>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();

    protected override AuditTrailOptions AuditOptions => _auditOptions;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("shop");
        modelBuilder.Entity<OrderLine>().HasKey(x => new { x.OrderId, x.LineNo });
    }
}

