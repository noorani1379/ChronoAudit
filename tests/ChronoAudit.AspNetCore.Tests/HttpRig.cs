using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using ChronoAudit.EntityFrameworkCore.Tests;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChronoAudit.AspNetCore.Tests;

/// <summary>The store tables on their own context, as a host would add them.</summary>
public sealed class StoreDb(DbContextOptions<StoreDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyChronoAuditStore(this);
    }
}

/// <summary>Who calls: turned into claims by the test authentication handler.</summary>
public sealed record Who(string? User, string? Group = null, bool All = false, string? Session = null, string? ActAs = null)
{
    public static readonly Who Anonymous = new((string?)null);
    public static readonly Who Admin = new("admin", All: true, Session: "s-admin");
    public static Who Me(string user = "u1") => new(user, Session: "s-" + user);
    public static Who InGroup(string user, string group) => new(user, group);
}

public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user) || string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", user.ToString()), new("name", "Name of " + user) };
        if (Request.Headers.TryGetValue("X-Test-Group", out var group)) { claims.Add(new Claim("group", group.ToString())); }
        if (Request.Headers.TryGetValue("X-Test-Scope", out var scope)) { claims.Add(new Claim("chronoaudit_scope", scope.ToString())); }
        if (Request.Headers.TryGetValue("X-Test-Session", out var session)) { claims.Add(new Claim("sid", session.ToString())); }
        if (Request.Headers.TryGetValue("X-Test-ActAs", out var actAs)) { claims.Add(new Claim("act_as", actAs.ToString())); }
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

/// <summary>A controller with a write action, to prove the MVC intent filter next to the minimal endpoint one.</summary>
[ApiController]
[Route("mvc/orders")]
public sealed class OrdersController : ControllerBase
{
    [HttpPost]
    public IActionResult Create([FromBody] OrderBody body) => Ok(new { received = body.Title });

    [HttpGet]
    public IActionResult List() => Ok(Array.Empty<string>());

    public sealed record OrderBody(string Title);
}

/// <summary>A real web host on the test server: SQLite in memory, the ChronoAudit endpoints under two policies, a few sample endpoints.</summary>
public sealed class HttpRig : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _store = new("DataSource=:memory:");
    private readonly SqliteConnection _shop = new("DataSource=:memory:");
    private WebApplication? _app;

    private HttpRig(string exportFolder) => ExportFolder = exportFolder;

    public string ExportFolder { get; }

    public FakeClock Clock { get; } = new(T0);

    public CapturingLoggerProvider Logs { get; } = new();

    public WebApplication App => _app!;

    private static IResult Boom() => throw new InvalidOperationException("kaboom");

    // The body is read by the endpoint itself, as many real endpoints do.
    private static async Task<IResult> EchoAsync(HttpContext http, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(http.Request.Body);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return Results.Text(text, "application/json");
    }

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _app!.Services;

    public static async Task<HttpRig> StartAsync(
        Action<ChronoAuditBuilder>? customize = null,
        Action<IServiceCollection>? services = null,
        Action<ChronoAuditAspNetCoreOptions>? aspNet = null,
        Action<ChronoAuditOptions>? configure = null,
        Action<WebApplication>? endpoints = null,
        bool exceptionHandler = true,
        Action<WebApplication>? beforeAuth = null)
    {
        var rig = new HttpRig(Path.Combine(Path.GetTempPath(), "chronoaudit-http-" + Guid.NewGuid().ToString("N")));
        await rig.BuildAsync(customize, services, aspNet, configure, endpoints, exceptionHandler, beforeAuth);
        return rig;
    }

    private async Task BuildAsync(
        Action<ChronoAuditBuilder>? customize, Action<IServiceCollection>? extraServices, Action<ChronoAuditAspNetCoreOptions>? aspNet,
        Action<ChronoAuditOptions>? configure, Action<WebApplication>? extraEndpoints, bool exceptionHandler, Action<WebApplication>? beforeAuth)
    {
        _store.Open();
        _shop.Open();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        var services = builder.Services;
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddDbContext<StoreDb>(o => o.UseSqlite(_store).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>());
        services.AddDbContext<ShopDb>(o => o.UseSqlite(_shop).ReplaceService<IModelCustomizer, SqliteTestModelCustomizer>());

        var options = new ChronoAuditOptions();
        options.Exports.Directory = ExportFolder;
        configure?.Invoke(options);
        var chrono = services.AddChronoAudit(SqliteShop.Key, options).AddModule<ShopDb>("shop").UseStore<StoreDb>().AddActivityLog();
        chrono.AddBuiltInAnalytics().AddSavedReports().AddExports().AddTimeline().AddDataLag();
        customize?.Invoke(chrono);
        if (aspNet is null)
        {
            services.AddChronoAuditAspNetCore();
        }
        else
        {
            services.AddChronoAuditAspNetCore(aspNet);
        }

        services.AddChronoAuditMutationIntents();
        services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
        services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy("chronoaudit", p => p.RequireAuthenticatedUser())
            .AddPolicy("deny", p => p.RequireAssertion(_ => false));
        extraServices?.Invoke(services);

        var app = builder.Build();
        app.UseChronoAuditActivityLog();
        if (exceptionHandler)
        {
            app.UseExceptionHandler(handler => handler.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }));
        }

        beforeAuth?.Invoke(app);
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapChronoAudit("chronoaudit", "chronoaudit");
        app.MapChronoAudit("deny", "denied");
        app.MapControllers();
        var sample = app.MapGroup("api").WithChronoAuditMutationIntents();
        sample.MapPost("echo", EchoAsync);
        sample.MapGet("hello", () => Results.Ok(new { hello = "world" }));
        sample.MapPost("bound", (BoundBody body) => Results.Ok(new { received = body.Title }));
        sample.MapGet("boom", Boom);
        sample.MapGet("teapot", (HttpContext http) =>
        {
            http.Items[ChronoAuditHttpItems.ErrorCode] = "Sample.Teapot";
            return Results.Problem(statusCode: 418, title: "Sample.Teapot");
        });
        sample.MapGet("items/{id}", (string id) => Results.Ok(new { id }));
        sample.MapGet("ping/{name}", (string name) => Results.Ok(new { name }));
        sample.MapPost("ignore", () => Results.Ok(new { ignored = true }));
        sample.MapPost("auth/login", EchoAsync);
        sample.MapPost("form", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            return Results.Ok(new { fields = form.Count, files = form.Files.Count });
        });
        sample.MapPost("bytes", async (HttpContext http) =>
        {
            var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer);
            return Results.Ok(new { length = buffer.Length });
        });
        sample.MapGet("wait", async (HttpContext http) =>
        {
            await Task.Delay(Timeout.Infinite, http.RequestAborted);
            return Results.Ok();
        });
        app.MapGet("internal/status", () => "ok");
        app.MapPost("health/ping", () => Results.Ok()).WithChronoAuditMutationIntents();
        app.MapGet("health", () => "ok");
        extraEndpoints?.Invoke(app);

        await app.StartAsync();
        _app = app;
        Client = app.GetTestClient();
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StoreDb>().Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<ShopDb>().Database.EnsureCreatedAsync();
    }

    public HttpRequestMessage Request(HttpMethod method, string url, Who? who = null, object? json = null, string? rawJson = null)
    {
        var message = new HttpRequestMessage(method, url);
        if (who?.User is { } user)
        {
            message.Headers.Add("X-Test-User", user);
            if (who.Group is not null) { message.Headers.Add("X-Test-Group", who.Group); }
            if (who.All) { message.Headers.Add("X-Test-Scope", "all"); }
            if (who.Session is not null) { message.Headers.Add("X-Test-Session", who.Session); }
            if (who.ActAs is not null) { message.Headers.Add("X-Test-ActAs", who.ActAs); }
        }

        if (json is not null || rawJson is not null)
        {
            message.Content = new StringContent(rawJson ?? JsonSerializer.Serialize(json, ChronoAuditJsonForTests.Options), Encoding.UTF8, "application/json");
        }

        return message;
    }

    public Task<HttpResponseMessage> GetAsync(string url, Who? who = null) => Client.SendAsync(Request(HttpMethod.Get, url, who));

    public Task<HttpResponseMessage> PostAsync(string url, Who? who = null, object? json = null, string? rawJson = null)
        => Client.SendAsync(Request(HttpMethod.Post, url, who, json, rawJson));

    public static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public async Task<T> ReadStoreAsync<T>(Func<StoreDb, Task<T>> read)
    {
        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<StoreDb>());
    }

    public async Task<IReadOnlyList<AuditLogEntry>> AuditRowsAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDb>().AuditLogEntries.AsNoTracking().OrderBy(a => a.SequenceNumber).ToListAsync();
    }

    public async Task<IReadOnlyList<ApiRequestLog>> LogsAsync() => await ReadStoreAsync(db => db.Set<ApiRequestLog>().AsNoTracking().OrderBy(l => l.Id).ToListAsync());

    public async Task<IReadOnlyList<ApiMutationIntent>> IntentsAsync() => await ReadStoreAsync(db => db.Set<ApiMutationIntent>().AsNoTracking().OrderBy(l => l.Id).ToListAsync());

    /// <summary>Runs the export worker until the queue is empty (the real worker is not started in tests).</summary>
    public async Task RunExportsAsync()
    {
        var worker = new ExportJobWorker(
            Services.GetRequiredService<ChronoAuditStoreRegistration>(),
            Services.GetRequiredService<ChronoAuditRegistry>(),
            Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<ExportOptions>(),
            Services.GetRequiredService<ExportFileStore>(),
            Clock,
            Services.GetRequiredService<ILogger<ExportJobWorker>>());
        for (var i = 0; i < 10 && await worker.RunOnceAsync(CancellationToken.None); i++)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await _store.DisposeAsync();
        await _shop.DisposeAsync();
        if (Directory.Exists(ExportFolder))
        {
            Directory.Delete(ExportFolder, recursive: true);
        }
    }
}

internal static class ChronoAuditJsonForTests
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}

/// <summary>A JSON body that minimal API parameter binding reads completely before the endpoint filters run.</summary>
public sealed record BoundBody(string Title);
