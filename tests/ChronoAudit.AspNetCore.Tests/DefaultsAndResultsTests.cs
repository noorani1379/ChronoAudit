using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using ChronoAudit.EntityFrameworkCore.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class EndpointAuthTests
{
    private static readonly string Range = "from=2026-10-01T00:00:00Z&to=2026-12-01T00:00:00Z";

    private static readonly (string Group, string Url)[] Routes =
    [
        ("history", "history/tables"),
        ("audit", "audit/shop/search?" + "from=2026-10-01T00:00:00Z&to=2026-12-01T00:00:00Z"),
        ("activity", "activity/search?" + "from=2026-10-01T00:00:00Z&to=2026-12-01T00:00:00Z"),
        ("analytics", "analytics/catalog"),
        ("saved-reports", "saved-reports"),
        ("exports", "exports"),
        ("extract", "extract/csv?" + "from=2026-10-01T00:00:00Z&to=2026-12-01T00:00:00Z"),
        ("timeline", "timeline/groups/g1"),
    ];

    [Fact]
    public async Task EveryGroup_Answers401ToAnAnonymousCaller_And403WhenThePolicyDenies()
    {
        await using var rig = await HttpRig.StartAsync();

        foreach (var (group, url) in Routes)
        {
            var anonymous = await rig.GetAsync("/chronoaudit/" + url);
            var denied = await rig.GetAsync("/denied/" + url, Who.Me());
            var anonymousDenied = await rig.GetAsync("/denied/" + url);

            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.True(anonymousDenied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, group);
        }

        Assert.NotEmpty(Range);
    }

    [Fact]
    public async Task EveryGroup_IsOpenToAnAuthenticatedCallerThePolicyAllows()
    {
        await using var rig = await HttpRig.StartAsync();

        foreach (var (group, url) in Routes)
        {
            var response = await rig.GetAsync("/chronoaudit/" + url, Who.Admin);

            Assert.True(response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden && (int)response.StatusCode < 500, $"{group}: {(int)response.StatusCode}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MappingAGroupWithoutAPolicy_IsRefused(string? policy)
    {
        await using var rig = await HttpRig.StartAsync();
        IEndpointRouteBuilder routes = rig.App;

        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditHistory(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditAudit(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditActivity(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditAnalytics(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditSavedReports(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditExports(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditExtract(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAuditTimeline(policy!));
        Assert.Throws<ArgumentException>(() => routes.MapChronoAudit(policy!));
    }

    [Fact]
    public async Task TheRouteTable_HasEveryDocumentedRoute()
    {
        await using var rig = await HttpRig.StartAsync();

        var routes = rig.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("chronoaudit/", StringComparison.Ordinal))
            .Select(e => string.Join('|', e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods) + " " + e.RoutePattern.RawText!.TrimEnd('/'))
            .ToHashSet();

        string[] expected =
        [
            "GET chronoaudit/history/tables",
            "GET chronoaudit/history/{module}/tables/{table}/changes",
            "GET chronoaudit/history/{module}/tables/{table}/records/{key}/changes",
            "GET chronoaudit/audit/{module}/search",
            "POST chronoaudit/audit/verify",
            "GET chronoaudit/activity/search",
            "GET chronoaudit/activity/export.csv",
            "GET chronoaudit/activity/statistics",
            "GET chronoaudit/activity/requests/{logId:long}/body",
            "GET chronoaudit/activity/intents/incomplete",
            "GET chronoaudit/analytics/catalog",
            "POST chronoaudit/analytics/run",
            "GET chronoaudit/saved-reports",
            "POST chronoaudit/saved-reports",
            "GET chronoaudit/saved-reports/{id:guid}",
            "PUT chronoaudit/saved-reports/{id:guid}",
            "DELETE chronoaudit/saved-reports/{id:guid}",
            "POST chronoaudit/saved-reports/{id:guid}/run",
            "POST chronoaudit/exports",
            "GET chronoaudit/exports",
            "GET chronoaudit/exports/{id:guid}",
            "POST chronoaudit/exports/{id:guid}/cancel",
            "GET chronoaudit/exports/{id:guid}/download",
            "GET chronoaudit/extract/csv",
            "GET chronoaudit/timeline/subjects/{subjectType}/{subjectId}/evidence",
            "GET chronoaudit/timeline/subjects/{subjectType}/{subjectId}",
            "GET chronoaudit/timeline/groups/{groupKey}",
            "GET chronoaudit/timeline/actors/{actorUserId}",
        ];
        foreach (var route in expected)
        {
            Assert.Contains(route, routes);
        }
    }
}

public sealed class DefaultsTests
{
    private static (HttpContextAuditActor Actor, DefaultHttpContext Http) ActorFor(ChronoAuditAspNetCoreOptions? options = null, params Claim[] claims)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        return (new HttpContextAuditActor(new FixedAccessor(http), options ?? new ChronoAuditAspNetCoreOptions()), http);
    }

    [Fact]
    public void TheActor_ReadsTheClaims_TheIp_AndTheSession()
    {
        var (actor, _) = ActorFor(null, new Claim("sub", "u1"), new Claim("name", "Ada"), new Claim("role", "clerk"), new Claim("sid", "s-9"));

        Assert.Equal(("u1", "Ada", "clerk", "s-9", "203.0.113.9"), (actor.UserId, actor.DisplayName, actor.Role, actor.SessionId, actor.UserIp));
        Assert.Equal(("u1", false), (actor.ActorUserId, actor.IsDelegated));
    }

    [Fact]
    public void TheActor_FallsBackToTheFrameworkClaimTypes_AndFirstMatchWins()
    {
        var (actor, _) = ActorFor(null, new Claim(ClaimTypes.NameIdentifier, "u2"), new Claim(ClaimTypes.Name, "Bob"), new Claim(ClaimTypes.Role, "admin"));
        var (both, _) = ActorFor(null, new Claim(ClaimTypes.NameIdentifier, "other"), new Claim("sub", "u3"));

        Assert.Equal(("u2", "Bob", "admin"), (actor.UserId, actor.DisplayName, actor.Role));
        Assert.Equal("u3", both.UserId);
    }

    [Fact]
    public void ADelegatedCall_ActsAsTheOtherUser_ButKeepsTheSignedInOne()
    {
        var (actor, _) = ActorFor(null, new Claim("sub", "assistant"), new Claim("act_as", "boss"));

        Assert.Equal(("assistant", "boss", true), (actor.UserId, actor.ActorUserId, actor.IsDelegated));
    }

    [Fact]
    public void ACustomClaimConfiguration_IsHonoured()
    {
        var options = new ChronoAuditAspNetCoreOptions { SessionIdClaimType = "session", ActAsUserIdClaimType = "on_behalf_of" };
        options.UserIdClaimTypes.Clear();
        options.UserIdClaimTypes.Add("uid");
        var (actor, _) = ActorFor(options, new Claim("uid", "u7"), new Claim("session", "x1"), new Claim("on_behalf_of", "u8"));

        Assert.Equal(("u7", "x1", "u8"), (actor.UserId, actor.SessionId, actor.ActorUserId));
    }

    [Fact]
    public void AnAnonymousRequest_HasNoActor()
    {
        var (actor, _) = ActorFor();

        Assert.Null(actor.UserId);
        Assert.Null(actor.DisplayName);
        Assert.Equal("203.0.113.9", actor.UserIp);
    }

    [Fact]
    public void TheCorrelationId_IsTheHeaderWhenItIsAGuid_ElseANewOne_AndIsSetOnTheResponse()
    {
        var id = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Correlation-Id"] = id.ToString("D");
        var fromHeader = new HttpCorrelationContext(new FixedAccessor(http), new ChronoAuditAspNetCoreOptions());
        var bad = new DefaultHttpContext();
        bad.Request.Headers["X-Correlation-Id"] = "not-a-guid";
        var generated = new HttpCorrelationContext(new FixedAccessor(bad), new ChronoAuditAspNetCoreOptions());
        var none = new HttpCorrelationContext(new FixedAccessor(null), new ChronoAuditAspNetCoreOptions());

        Assert.Equal(id, fromHeader.CorrelationId);
        Assert.Equal(id.ToString("D"), http.Response.Headers["X-Correlation-Id"].ToString());
        Assert.NotEqual(Guid.Empty, generated.CorrelationId);
        Assert.Equal(generated.CorrelationId.ToString("D"), bad.Response.Headers["X-Correlation-Id"].ToString());
        Assert.NotEqual(Guid.Empty, none.CorrelationId);
    }

    [Theory]
    [InlineData("all", "g1", ScopeKind.All)]
    [InlineData(null, "g1", ScopeKind.Group)]
    [InlineData(null, null, ScopeKind.Own)]
    [InlineData("ALL", null, ScopeKind.All)]
    [InlineData("some", null, ScopeKind.Own)]
    public async Task TheDefaultAccessScope_IsAllGroupOrOwn_FromTheClaims(string? scopeClaim, string? group, ScopeKind expected)
    {
        var claims = new List<Claim> { new("sub", "u1") };
        if (scopeClaim is not null) { claims.Add(new Claim("chronoaudit_scope", scopeClaim)); }
        if (group is not null) { claims.Add(new Claim("group", group)); }

        var scope = await new ClaimsAccessScope(new ChronoAuditAspNetCoreOptions()).GetScopeAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "t")), CancellationToken.None);

        Assert.Equal((expected, "u1"), (scope.Kind, scope.UserId));
        Assert.Equal(expected == ScopeKind.Group ? group : null, scope.GroupId);
    }

    [Fact]
    public void AHostRegistration_Wins_AndTheDefaultsFillTheRest()
    {
        var own = new OwnActor();
        var services = new ServiceCollection();
        services.AddScoped<IAuditActor>(_ => own);

        services.AddChronoAuditAspNetCore();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Same(own, scope.ServiceProvider.GetRequiredService<IAuditActor>());
        Assert.IsType<HttpCorrelationContext>(scope.ServiceProvider.GetRequiredService<ICorrelationContext>());
        Assert.IsType<ClaimsAccessScope>(provider.GetRequiredService<IChronoAuditAccessScope>());
    }

    [Fact]
    public void TheOptionsCanComeFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChronoAudit:AspNetCore:GroupIdClaimType"] = "team",
            ["ChronoAudit:AspNetCore:CorrelationHeader"] = "X-Request-Trace",
            ["ChronoAudit:AspNetCore:UserIdClaimTypes:0"] = "uid",
            ["ChronoAudit:AspNetCore:UserIdClaimTypes:1"] = "sub",
        }).Build();
        var services = new ServiceCollection();

        services.AddChronoAuditAspNetCore(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<ChronoAuditAspNetCoreOptions>();
        Assert.Equal(("team", "X-Request-Trace"), (options.GroupIdClaimType, options.CorrelationHeader));
        Assert.Equal("uid,sub", string.Join(",", options.UserIdClaimTypes));
    }

    private sealed class OwnActor : IAuditActor
    {
        public string? UserId => "own";

        public string? DisplayName => "Own";
    }
}

public sealed class ProblemDetailsTests
{
    [Theory]
    [InlineData(ChronoAuditErrorKind.Validation, 400)]
    [InlineData(ChronoAuditErrorKind.Unauthorized, 401)]
    [InlineData(ChronoAuditErrorKind.Forbidden, 403)]
    [InlineData(ChronoAuditErrorKind.NotFound, 404)]
    [InlineData(ChronoAuditErrorKind.Conflict, 409)]
    [InlineData(ChronoAuditErrorKind.Unprocessable, 422)]
    [InlineData(ChronoAuditErrorKind.Unavailable, 503)]
    public void EachKind_MapsToItsStatus(ChronoAuditErrorKind kind, int status)
    {
        Assert.Equal(status, ChronoAuditHttpResults.StatusOf(kind));
    }

    [Fact]
    public async Task AFailure_IsProblemDetails_WithTheStableCodeAsTitle_AndItIsRecordedInTheActivityLog()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.GetAsync("/chronoaudit/saved-reports/" + Guid.NewGuid(), Who.Me());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal("SavedReport.NotFound", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
        var log = Assert.Single(await rig.LogsAsync());
        Assert.Equal(("SavedReport.NotFound", (short)404), (log.ErrorCode, log.StatusCode));
    }

    [Fact]
    public async Task TheHostCanDescribeErrorsInItsOwnWords()
    {
        await using var rig = await HttpRig.StartAsync(aspNet: o => o.DescribeError = e => "[fa] " + e.Code);

        var response = await rig.GetAsync("/chronoaudit/saved-reports/" + Guid.NewGuid(), Who.Me());

        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal("[fa] SavedReport.NotFound", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ACrash_NeverShowsAStackTraceOrSql_AndIsRecordedAs500()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.GetAsync("/api/boom", Who.Me());

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("kaboom", text, StringComparison.Ordinal);
        Assert.DoesNotContain("at ChronoAudit", text, StringComparison.Ordinal);
        Assert.Equal((short)500, (await rig.LogsAsync()).Single().StatusCode);
    }

    [Fact]
    public async Task ASuccess_IsJson_WithEnumsAsText()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.GetAsync("/chronoaudit/analytics/catalog", Who.Admin);

        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var activity = body.RootElement.EnumerateArray().Single(e => e.GetProperty("key").GetString() == "activity");
        Assert.Equal(JsonValueKind.String, activity.GetProperty("measures")[0].GetProperty("aggregation").ValueKind);
        Assert.Equal("Count", activity.GetProperty("measures")[0].GetProperty("aggregation").GetString());
    }

    [Fact]
    public async Task ARefusedRequest_IsProblemJson_WithTheValidationCode()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.PostAsync("/chronoaudit/exports", Who.Me(), new { producer = "analytics", format = "Csv", parameters = new { } });

        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ChronoAudit.Validation", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ABadJsonBody_IsA400_NotA500()
    {
        await using var rig = await HttpRig.StartAsync();

        var response = await rig.PostAsync("/chronoaudit/analytics/run", Who.Me(), rawJson: "{not json");
        var empty = await rig.PostAsync("/chronoaudit/analytics/run", Who.Me(), rawJson: "null");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var body = await HttpRig.JsonAsync(response);
        Assert.Equal("ChronoAudit.Validation", body.RootElement.GetProperty("title").GetString());
        _ = JsonDocument.Parse("{}");
    }
}

// HttpContextAccessor keeps its context in one static AsyncLocal, so two accessors in one test would share it.
internal sealed class FixedAccessor(HttpContext? context) : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; } = context;
}
