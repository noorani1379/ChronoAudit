using System.Security.Claims;

namespace ChronoAudit.AspNetCore;

/// <summary>Which claims and headers the ASP.NET Core package reads (section <c>ChronoAudit:AspNetCore</c>).</summary>
public sealed class ChronoAuditAspNetCoreOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:AspNetCore";

    /// <summary>Claim types that hold the user id, first match wins. Default <c>sub</c> and the .NET name identifier.</summary>
    public IList<string> UserIdClaimTypes { get; } = new List<string> { "sub", ClaimTypes.NameIdentifier };

    /// <summary>Claim types that hold the display name, first match wins.</summary>
    public IList<string> NameClaimTypes { get; } = new List<string> { "name", ClaimTypes.Name };

    /// <summary>Claim types that hold the role, first match wins.</summary>
    public IList<string> RoleClaimTypes { get; } = new List<string> { "role", ClaimTypes.Role };

    /// <summary>The claim that holds the group id of the user. Default <c>group</c>.</summary>
    public string GroupIdClaimType { get; set; } = "group";

    /// <summary>The claim that holds the session id. Default <c>sid</c>.</summary>
    public string SessionIdClaimType { get; set; } = "sid";

    /// <summary>The claim that holds the id of the user on whose behalf the signed-in user acts. Default <c>act_as</c>.</summary>
    public string ActAsUserIdClaimType { get; set; } = "act_as";

    /// <summary>The claim the default access scope reads: the value <c>all</c> gives the All scope. Default <c>chronoaudit_scope</c>.</summary>
    public string ScopeClaimType { get; set; } = "chronoaudit_scope";

    /// <summary>The request and response header of the correlation id. Default <c>X-Correlation-Id</c>.</summary>
    public string CorrelationHeader { get; set; } = "X-Correlation-Id";

    /// <summary>Turns an error into the text of the ProblemDetails <c>detail</c>, for example to translate it; null uses the message of the error.</summary>
    public Func<ChronoAuditError, string>? DescribeError { get; set; }

    internal string? FirstClaim(ClaimsPrincipal user, IEnumerable<string> types)
    {
        foreach (var type in types)
        {
            var value = user.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
