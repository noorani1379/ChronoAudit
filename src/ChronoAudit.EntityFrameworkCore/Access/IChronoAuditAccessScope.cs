using System.Security.Claims;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>
/// Says which data a signed-in user may see: the Own, Group or All scope. The HTTP endpoints ask it for every call. The ASP.NET Core
/// package ships a claims based default; a host that keeps its permissions elsewhere replaces it.
/// </summary>
public interface IChronoAuditAccessScope
{
    /// <summary>The data scope of a user.</summary>
    /// <param name="user">The signed-in user of the request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The scope.</returns>
    Task<ChronoAuditScope> GetScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}
