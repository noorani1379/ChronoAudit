using ChronoAudit;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChronoAudit.Sample.WebApi;

public sealed class Book : AuditableEntityBase
{
    public int Id { get; set; }

    public required string Title { get; set; }

    public required string Isbn { get; set; }

    public required string Author { get; set; }
}

public sealed class Member : AuditableEntityBase
{
    public int Id { get; set; }

    public required string FullName { get; set; }

    public required string Email { get; set; }

    public DateTimeOffset JoinedAtUtc { get; set; }

    public MemberContact? Contact { get; set; }
}

public sealed class MemberContact : AuditableEntityBase
{
    public int MemberId { get; set; }

    [SensitiveData]
    public required string PhoneNumber { get; set; }

    public required string Address { get; set; }
}

public sealed class Loan : AuditableEntityBase
{
    public int BookId { get; set; }

    public int MemberId { get; set; }

    public DateTimeOffset LoanedAtUtc { get; set; }

    public DateTimeOffset DueAtUtc { get; set; }

    public DateTimeOffset? ReturnedAtUtc { get; set; }
}

public sealed class LibraryDbContext : AuditableDbContext
{
    public LibraryDbContext(
        DbContextOptions<LibraryDbContext> options,
        IAuditActor actor,
        ICorrelationContext correlationContext,
        IAuditLogAccessor auditLogAccessor,
        AuditSignalChannel signalChannel,
        TimeProvider? clock = null,
        IChronoAuditCalendar? calendar = null)
        : base(options, actor, correlationContext, auditLogAccessor, signalChannel, clock, calendar)
    {
    }

    // Constructor used by design-time factory and tests
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options)
        : base(options, new DesignTimeActor(), new DesignTimeCorrelation(), new DesignTimeAccessor(), new AuditSignalChannel())
    {
    }

    public DbSet<Book> Books => Set<Book>();

    public DbSet<Member> Members => Set<Member>();

    public DbSet<MemberContact> MemberContacts => Set<MemberContact>();

    public DbSet<Loan> Loans => Set<Loan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("library");

        modelBuilder.Entity<Book>(b =>
        {
            b.ToTable("Books", "library");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(256).IsRequired();
            b.Property(x => x.Isbn).HasMaxLength(32).IsRequired();
            b.Property(x => x.Author).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<Member>(b =>
        {
            b.ToTable("Members", "library");
            b.HasKey(x => x.Id);
            b.Property(x => x.FullName).HasMaxLength(128).IsRequired();
            b.Property(x => x.Email).HasMaxLength(256).IsRequired();
            b.HasOne(x => x.Contact).WithOne().HasForeignKey<MemberContact>(x => x.MemberId);
        });

        modelBuilder.Entity<MemberContact>(b =>
        {
            b.ToTable("MemberContacts", "library");
            b.HasKey(x => x.MemberId);
            b.Property(x => x.PhoneNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.Address).HasMaxLength(512).IsRequired();
        });

        modelBuilder.Entity<Loan>(b =>
        {
            b.ToTable("Loans", "library");
            b.HasKey(x => new { x.BookId, x.MemberId, x.LoanedAtUtc });
        });

        modelBuilder.ApplyChronoAuditStore(this, new ChronoAuditStoreOptions { Schema = "chronoaudit" });
    }

    private sealed class DesignTimeActor : IAuditActor
    {
        public string? UserId => "design-time";
        public string? DisplayName => "Design Time";
    }

    private sealed class DesignTimeCorrelation : ICorrelationContext
    {
        public Guid CorrelationId => Guid.Empty;
    }

    private sealed class DesignTimeAccessor : IAuditLogAccessor
    {
        public IReadOnlyCollection<AuditLogEntry> GetPendingLogs() => [];
        public void Clear() { }
    }
}

public sealed class LibraryDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    public LibraryDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("CHRONOAUDIT_SAMPLE_PROVIDER")
            ?? (args.Length > 0 ? args[0] : "SqlServer");

        var optionsBuilder = new DbContextOptionsBuilder<LibraryDbContext>();

        if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            optionsBuilder.UseNpgsql(
                "Host=localhost;Database=ChronoAuditLibrary_Design;Username=postgres;Password=postgres",
                npgsql =>
                {
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "library");
                    npgsql.MigrationsAssembly("ChronoAudit.Sample.WebApi");
                });
        }
        else
        {
            optionsBuilder.UseSqlServer(
                "Server=localhost;Database=ChronoAuditLibrary_Design;Trusted_Connection=True;TrustServerCertificate=True",
                sql =>
                {
                    sql.MigrationsHistoryTable("__EFMigrationsHistory", "library");
                    sql.MigrationsAssembly("ChronoAudit.Sample.WebApi");
                });
        }

        optionsBuilder.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly, ProviderMigrationsAssembly>();

        return new LibraryDbContext(optionsBuilder.Options);
    }
}
