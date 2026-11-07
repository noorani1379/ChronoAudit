using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChronoAudit.EntityFrameworkCore.Tests;

// A tiny neutral "shop" that an adopter might own: it uses nothing but the audit library.

/// <summary>A soft-deletable, audited entity with one secret column.</summary>
public sealed class Order : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "";

    public int Quantity { get; set; }

    [SensitiveData]
    public string Secret { get; set; } = "";
}

/// <summary>An audited entity with a composite key that is not soft deletable.</summary>
public sealed class OrderLine
{
    public Guid OrderId { get; set; }

    public int LineNo { get; set; }

    public string Sku { get; set; } = "";
}

/// <summary>Evidence rows that can only be inserted.</summary>
public sealed class Receipt : IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = "";
}

/// <summary>A high-volume table that is its own record.</summary>
public sealed class Noise : IExcludedFromAutoAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Number { get; set; }
}

/// <summary>An entity whose creator is stored as the actor's role, not the person.</summary>
[AuditActorNameAsRole]
public sealed class Payment : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public decimal Amount { get; set; }
}

/// <summary>An entity whose key the database generates (an identity column): the case where the key is unknown until the save.</summary>
public sealed class Ticket : AuditableEntityBase
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public List<TicketNote> Notes { get; set; } = [];
}

/// <summary>A child with a client-made key that points at a ticket whose key the database makes.</summary>
public sealed class TicketNote : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int TicketId { get; set; }

    public Ticket? Ticket { get; set; }

    public string Text { get; set; } = "";
}

public sealed class ShopDb : AuditableDbContext
{
    private readonly AuditTrailOptions _auditOptions;

    public ShopDb(
        DbContextOptions<ShopDb> options,
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

    public List<string> Calls { get; } = [];

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<Noise> Noises => Set<Noise>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();

    protected override AuditTrailOptions AuditOptions => _auditOptions;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<OrderLine>().HasKey(x => new { x.OrderId, x.LineNo });
    }

    protected override Task OnBeforeSaveAsync(CancellationToken cancellationToken)
    {
        Calls.Add("before-save");
        return base.OnBeforeSaveAsync(cancellationToken);
    }

    protected override Task OnBeforeAuditAsync(CancellationToken cancellationToken)
    {
        Calls.Add("before-audit");
        return base.OnBeforeAuditAsync(cancellationToken);
    }

    protected override void OnAfterSaved() => Calls.Add("after-saved");
}

/// <summary>A customer with personal data: a phone number (number field), a full name (text field) and a secret.</summary>
public sealed class CustomerProfile : AuditableEntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string PhoneNumber { get; set; } = "";

    public string FullName { get; set; } = "";

    public string City { get; set; } = "";

    [SensitiveData]
    public string Pin { get; set; } = "";
}
