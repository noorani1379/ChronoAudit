using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ChronoAudit;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using ChronoAudit.Sample.WebApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Configuration
var provider = builder.Configuration["ChronoAudit:Provider"] ?? "SqlServer";
var connStr = builder.Configuration.GetConnectionString("LibraryDb");
if (string.IsNullOrWhiteSpace(connStr) || connStr == "<set in environment>")
{
    connStr = string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase)
        ? "Host=localhost;Port=55432;Database=chronoaudit_library_sample;Username=postgres"
        : "Server=localhost;Database=ChronoAuditLibrary_Sample;Trusted_Connection=True;TrustServerCertificate=True";
}

builder.Services.AddDbContext<LibraryDbContext>(options =>
{
    // One migration folder per provider lives in this assembly; EF Core must see only the folder of the provider in use.
    options.ReplaceService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly, ProviderMigrationsAssembly>();

    if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connStr, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "library"));
    }
    else
    {
        options.UseSqlServer(connStr, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "library"));
    }
});

// 2. Authentication & Authorization
builder.Services.AddAuthentication("Demo")
    .AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>("Demo", null);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("chronoaudit", policy => policy.RequireAuthenticatedUser());
});

// 3. CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders(ChronoAuditHttpHeaders.ExposedToBrowsers.ToArray());
    });
});

// 4. Personal Data Reveal Policy
builder.Services.AddSingleton<IPersonalDataRevealPolicy, SamplePersonalDataRevealPolicy>();

// 5. ChronoAudit Pipeline
builder.Services.AddChronoAudit(builder.Configuration, builder.Environment)
    .AddModule<LibraryDbContext>("library")
    .UseStore<LibraryDbContext>()
    .AddActivityLog()
    .AddHashWorkers()
    .AddAnchorWorker()
    .AddBuiltInAnalytics()
    .AddSavedReports()
    .AddExports()
    .AddExportWorker()
    .AddTimeline()
    .AddConsistencyWorker();

builder.Services.AddChronoAuditAspNetCore();
builder.Services.AddChronoAuditMutationIntents();

var app = builder.Build();

app.UseCors();
// Before authentication, so a call that is refused (401, 403) is recorded as well.
app.UseChronoAuditActivityLog();
app.UseAuthentication();
app.UseAuthorization();

// Map ChronoAudit Endpoints
app.MapChronoAudit("chronoaudit");

// Demo Library Endpoints
var books = app.MapGroup("/books")
    .RequireAuthorization("chronoaudit")
    .WithChronoAuditMutationIntents();

books.MapGet("", async (LibraryDbContext db, CancellationToken ct) =>
    await db.Books.AsNoTracking().ToListAsync(ct));

books.MapGet("{id:int}", async (int id, LibraryDbContext db, CancellationToken ct) =>
    await db.Books.FindAsync([id], ct) is { } book ? Results.Ok(book) : Results.NotFound());

books.MapPost("", async (CreateBookRequest request, LibraryDbContext db, ITimelineWriter timeline, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct) =>
{
    var book = new Book
    {
        Title = request.Title,
        Isbn = request.Isbn,
        Author = request.Author,
    };
    db.Books.Add(book);
    await db.SaveChangesAsync(ct);

    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "demo-user";
    await timeline.RecordAsync(new TimelineEventInput(
        SourceEventId: Guid.NewGuid(),
        EventType: "book.created",
        SubjectType: "Book",
        SubjectId: book.Id.ToString(CultureInfo.InvariantCulture),
        OccurredAtUtc: clock.GetUtcNow().ToUniversalTime(),
        ActorDisplayName: user.Identity?.Name ?? userId,
        Payload: JsonSerializer.Serialize(new { book.Title, book.Isbn }),
        GroupKey: book.Id.ToString(CultureInfo.InvariantCulture),
        Participants: [new TimelineParticipantInput(userId, "Creator")]),
        ct);

    return Results.Created($"/books/{book.Id}", book);
});

books.MapPut("{id:int}", async (int id, UpdateBookRequest request, LibraryDbContext db, ITimelineWriter timeline, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct) =>
{
    var book = await db.Books.FindAsync([id], ct);
    if (book is null) return Results.NotFound();

    book.Title = request.Title;
    book.Author = request.Author;
    await db.SaveChangesAsync(ct);

    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "demo-user";
    await timeline.RecordAsync(new TimelineEventInput(
        SourceEventId: Guid.NewGuid(),
        EventType: "book.updated",
        SubjectType: "Book",
        SubjectId: book.Id.ToString(CultureInfo.InvariantCulture),
        OccurredAtUtc: clock.GetUtcNow().ToUniversalTime(),
        ActorDisplayName: user.Identity?.Name ?? userId,
        Payload: JsonSerializer.Serialize(new { book.Title, book.Author }),
        GroupKey: book.Id.ToString(CultureInfo.InvariantCulture),
        Participants: [new TimelineParticipantInput(userId, "Editor")]),
        ct);

    return Results.Ok(book);
});

books.MapDelete("{id:int}", async (int id, LibraryDbContext db, ITimelineWriter timeline, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct) =>
{
    var book = await db.Books.FindAsync([id], ct);
    if (book is null) return Results.NotFound();

    db.Books.Remove(book); // triggers soft delete because Book derives from AuditableEntityBase
    await db.SaveChangesAsync(ct);

    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "demo-user";
    await timeline.RecordAsync(new TimelineEventInput(
        SourceEventId: Guid.NewGuid(),
        EventType: "book.deleted",
        SubjectType: "Book",
        SubjectId: book.Id.ToString(CultureInfo.InvariantCulture),
        OccurredAtUtc: clock.GetUtcNow().ToUniversalTime(),
        ActorDisplayName: user.Identity?.Name ?? userId,
        Payload: JsonSerializer.Serialize(new { book.Title }),
        GroupKey: book.Id.ToString(CultureInfo.InvariantCulture),
        Participants: [new TimelineParticipantInput(userId, "Remover")]),
        ct);

    return Results.NoContent();
});

app.Run();

namespace ChronoAudit.Sample.WebApi
{
    // Demo Requests
    public sealed record CreateBookRequest(string Title, string Isbn, string Author);
    public sealed record UpdateBookRequest(string Title, string Author);

    // Demo Authentication Handler
    public sealed class DemoAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Demo-User", out var userHeader) || string.IsNullOrWhiteSpace(userHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userName = userHeader.ToString();
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userName),
                new(ClaimTypes.Name, userName),
                new("chronoaudit_scope", "all"), // the claim ClaimsAccessScope reads; the demo user has the All scope
                new(ClaimTypes.Role, string.Equals(userName, "admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User"),
            };

            var identity = new ClaimsIdentity(claims, "Demo");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Demo");

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    // Demo Reveal Policy
    public sealed class SamplePersonalDataRevealPolicy : IPersonalDataRevealPolicy
    {
        public Task<bool> MayRevealAsync(
            ChronoAuditScope scope,
            string resourceType,
            CancellationToken cancellationToken)
            => Task.FromResult(scope.Kind == ScopeKind.All);
    }
}
