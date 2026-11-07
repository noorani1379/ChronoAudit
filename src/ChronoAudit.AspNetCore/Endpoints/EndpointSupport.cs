using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore;

/// <summary>The JSON options of the endpoints: web defaults and enums as text, whatever the host configured for its own APIs.</summary>
internal static class ChronoAuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>Helpers shared by the endpoint groups: the group, the scope, the allowed query keys, and typed query values.</summary>
internal static class EndpointSupport
{
    public static RouteGroupBuilder NewGroup(IEndpointRouteBuilder endpoints, string authorizationPolicy, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
        {
            throw new ArgumentException("An authorization policy is required: the ChronoAudit endpoints are never mapped without one.", nameof(authorizationPolicy));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var group = endpoints.MapGroup(pattern);
        group.RequireAuthorization(authorizationPolicy);
        group.AddEndpointFilter<DataLagHeaderFilter>();
        return group;
    }

    public static Task<ChronoAuditScope> ScopeAsync(HttpContext http)
        => http.RequestServices.GetRequiredService<IChronoAuditAccessScope>().GetScopeAsync(http.User, http.RequestAborted);

    // A misspelled filter is never silently ignored: any query key that the route does not declare is refused before anything is read.
    public static IResult? RefuseUnknownKeys(HttpContext http, params string[] allowed)
    {
        foreach (var key in http.Request.Query.Keys)
        {
            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return ChronoAuditHttpResults.Problem(http, FilterErrors.NotAllowed(key));
            }
        }

        return null;
    }

    public static IResult? RequireAll(HttpContext http, ChronoAuditScope scope)
        => scope.Kind == ScopeKind.All
            ? null
            : ChronoAuditHttpResults.Problem(http, ChronoAuditError.Forbidden("ChronoAudit.ScopeDenied", "This needs the All data scope."));

    public static IResult? RequireUser(HttpContext http, ChronoAuditScope scope)
        => scope.UserId is not null
            ? null
            : ChronoAuditHttpResults.Problem(http, ChronoAuditError.Validation("ChronoAudit.Validation", "A signed-in user is required."));

    public static string? Text(HttpContext http, string key)
        => http.Request.Query.TryGetValue(key, out var values) && values.ToString() is { Length: > 0 } text ? text : null;

    public static ChronoAuditResult<DateTimeOffset?> Date(HttpContext http, string key)
    {
        var text = Text(http, key);
        if (text is null)
        {
            return ChronoAuditResult.Success<DateTimeOffset?>(null);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? ChronoAuditResult.Success<DateTimeOffset?>(value)
            : ChronoAuditError.Validation("ChronoAudit.Validation", $"'{key}' is not a valid date and time.");
    }

    public static ChronoAuditResult<int> Int(HttpContext http, string key, int fallback)
    {
        var text = Text(http, key);
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : ChronoAuditError.Validation("ChronoAudit.Validation", $"'{key}' is not a whole number.");
    }

    public static ChronoAuditResult<bool> Bool(HttpContext http, string key)
    {
        var text = Text(http, key);
        if (text is null)
        {
            return false;
        }

        return bool.TryParse(text, out var value) ? value : ChronoAuditError.Validation("ChronoAudit.Validation", $"'{key}' must be true or false.");
    }

    public static ChronoAuditResult<Guid?> Guid(HttpContext http, string key)
    {
        var text = Text(http, key);
        if (text is null)
        {
            return ChronoAuditResult.Success<Guid?>(null);
        }

        return System.Guid.TryParse(text, out var value) ? ChronoAuditResult.Success<Guid?>(value) : ChronoAuditError.Validation("ChronoAudit.Validation", $"'{key}' is not a valid id.");
    }

    // A JSON body in the ChronoAudit options; an unreadable or empty body is a Validation error, never an exception.
    public static async Task<ChronoAuditResult<T>> BodyAsync<T>(HttpContext http)
        where T : class
    {
        try
        {
            var body = await http.Request.ReadFromJsonAsync<T>(ChronoAuditJson.Options, http.RequestAborted);
            return body is null ? ChronoAuditError.Validation("ChronoAudit.Validation", "A JSON body is required.") : body;
        }
        catch (JsonException)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The request body is not valid JSON for this route.");
        }
        catch (InvalidOperationException)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The request body must be JSON.");
        }
    }
}
