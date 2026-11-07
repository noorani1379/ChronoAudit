using System.Security.Claims;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;

namespace ChronoAudit.AspNetCore;

/// <summary>Keys of <c>HttpContext.Items</c> that connect the pieces of the package.</summary>
public static class ChronoAuditHttpItems
{
    /// <summary>The id of the request, shared by the activity log row and the mutation intent (a <see cref="Guid"/>).</summary>
    public const string RequestId = "ChronoAudit.RequestId";

    /// <summary>The stable error code of a failed call (a <see cref="string"/>), set by the ProblemDetails writer and read by the activity middleware.</summary>
    public const string ErrorCode = "ChronoAudit.ErrorCode";
}

/// <summary>Headers the package reads or writes.</summary>
public static class ChronoAuditHttpHeaders
{
    /// <summary>How many seconds the data lags behind (see <see cref="IChronoAuditDataLag"/>).</summary>
    public const string DataLagSeconds = "X-Data-Lag-Seconds";

    /// <summary>The SHA-256 of a downloaded export file, upper-case hex.</summary>
    public const string ContentSha256 = "X-Content-SHA256";

    /// <summary>
    /// The headers a browser client can read only when the CORS policy of the host exposes them:
    /// <c>policy.WithExposedHeaders(ChronoAuditHttpHeaders.ExposedToBrowsers.ToArray())</c>.
    /// </summary>
    public static IReadOnlyList<string> ExposedToBrowsers { get; } = [DataLagSeconds, ContentSha256, "Content-Disposition"];
}

/// <summary>Reads the actor of an audit row from the claims of the current request.</summary>
public sealed class HttpContextAuditActor(IHttpContextAccessor accessor, ChronoAuditAspNetCoreOptions options) : IAuditActor
{
    private HttpContext? Http => accessor.HttpContext;

    private ClaimsPrincipal? User => Http?.User;

    private string? SignedInUserId => User is { } user ? options.FirstClaim(user, options.UserIdClaimTypes) : null;

    private string? ActAs => User?.FindFirst(options.ActAsUserIdClaimType)?.Value is { Length: > 0 } value ? value : null;

    /// <inheritdoc />
    public string? UserId => SignedInUserId;

    /// <inheritdoc />
    public string? DisplayName => User is { } user ? options.FirstClaim(user, options.NameClaimTypes) : null;

    /// <inheritdoc />
    public string? UserIp => Http?.Connection.RemoteIpAddress?.ToString();

    /// <inheritdoc />
    public string? SessionId => User?.FindFirst(options.SessionIdClaimType)?.Value;

    /// <inheritdoc />
    public string? Role => User is { } user ? options.FirstClaim(user, options.RoleClaimTypes) : null;

    /// <inheritdoc />
    public string? RoleTitle => null;

    /// <inheritdoc />
    public string? ActorUserId => ActAs ?? SignedInUserId;

    /// <inheritdoc />
    public bool IsDelegated => ActAs is not null;

    /// <inheritdoc />
    public object? AuditMetadata => null;
}

/// <summary>
/// One correlation id per request: the value of the correlation header when it parses as a GUID, else a new one. The id is also set on the
/// response, so a client can quote it.
/// </summary>
public sealed class HttpCorrelationContext : ICorrelationContext
{
    /// <summary>Creates the context of the current request.</summary>
    /// <param name="accessor">The accessor of the current request.</param>
    /// <param name="options">The options (the header name).</param>
    public HttpCorrelationContext(IHttpContextAccessor accessor, ChronoAuditAspNetCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(options);
        var http = accessor.HttpContext;
        var header = http?.Request.Headers[options.CorrelationHeader].ToString();
        CorrelationId = Guid.TryParse(header, out var id) && id != Guid.Empty ? id : Guid.NewGuid();
        if (http is { Response.HasStarted: false })
        {
            http.Response.Headers[options.CorrelationHeader] = CorrelationId.ToString("D");
        }
    }

    /// <inheritdoc />
    public Guid CorrelationId { get; }
}

/// <summary>
/// The default access scope: All for a user with the claim <c>chronoaudit_scope=all</c>, Group for a user with a group claim, else Own.
/// A host that keeps permissions elsewhere registers its own <see cref="IChronoAuditAccessScope"/>.
/// </summary>
public sealed class ClaimsAccessScope(ChronoAuditAspNetCoreOptions options) : IChronoAuditAccessScope
{
    /// <inheritdoc />
    public Task<ChronoAuditScope> GetScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var userId = options.FirstClaim(user, options.UserIdClaimTypes);
        var group = user.FindFirst(options.GroupIdClaimType)?.Value;
        if (string.Equals(user.FindFirst(options.ScopeClaimType)?.Value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ChronoAuditScope.Everything(userId));
        }

        if (userId is not null && !string.IsNullOrWhiteSpace(group))
        {
            return Task.FromResult(ChronoAuditScope.GroupOf(userId, group));
        }

        return Task.FromResult(new ChronoAuditScope(ScopeKind.Own, userId, null));
    }
}
