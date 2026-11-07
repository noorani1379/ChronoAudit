using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.AspNetCore;

/// <summary>Turns a <see cref="ChronoAuditError"/> into an HTTP answer (ProblemDetails with the stable code as its title).</summary>
internal static class ChronoAuditHttpResults
{
    public static int StatusOf(ChronoAuditErrorKind kind) => kind switch
    {
        ChronoAuditErrorKind.Validation => StatusCodes.Status400BadRequest,
        ChronoAuditErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ChronoAuditErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ChronoAuditErrorKind.NotFound => StatusCodes.Status404NotFound,
        ChronoAuditErrorKind.Conflict => StatusCodes.Status409Conflict,
        ChronoAuditErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ChronoAuditErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    // The detail is the message of the error, or the text the host's DescribeError gives; never a stack trace and never SQL.
    public static IResult Problem(HttpContext http, ChronoAuditError error)
    {
        var options = http.RequestServices.GetRequiredService<ChronoAuditAspNetCoreOptions>();
        http.Items[ChronoAuditHttpItems.ErrorCode] = error.Code;
        return Results.Problem(statusCode: StatusOf(error.Kind), title: error.Code, detail: options.DescribeError?.Invoke(error) ?? error.Message);
    }

    public static IResult Of<T>(HttpContext http, ChronoAuditResult<T> result) => result.IsSuccess ? Results.Json(result.Value, ChronoAuditJson.Options) : Problem(http, result.Error);

    public static IResult Json<T>(T value) => Results.Json(value, ChronoAuditJson.Options);

    public static IResult Of(HttpContext http, ChronoAuditResult result) => result.IsSuccess ? Results.NoContent() : Problem(http, result.Error);

    public static ChronoAuditError Unavailable { get; } = ChronoAuditError.Unavailable(
        "ActivityLog.Unavailable", "The record of this request could not be stored, so the request was not run; please try again.");
}

/// <summary>Writes the mutation intent of a request: the fact that a write was attempted, before the write runs.</summary>
internal sealed class MutationIntentWriter(IApiMutationIntentStore store, ActivityLogOptions activityOptions, ChronoAuditAspNetCoreOptions options, TimeProvider clock, ILogger<MutationIntentWriter> logger)
{
    // The SHA-256 of zero bytes: a mutating call without a body still has a defined value.
    private static readonly string EmptyBodySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]));

    public static bool IsMutation(string method)
        => HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    public bool Applies(HttpContext http)
        => IsMutation(http.Request.Method)
           && !activityOptions.ExcludedPathPrefixes.Any(prefix => http.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stores the intent; false when it could not be stored (the call must then not run).</summary>
    public async Task<bool> TryWriteAsync(HttpContext http)
    {
        try
        {
            // The caller may go away; the intent is still written, so the request token must not cancel it.
            await store.WriteAsync(Describe(http), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The mutation intent for {Method} {Path} could not be stored; the call is refused.", http.Request.Method, http.Request.Path.Value);
            http.Items[ChronoAuditHttpItems.ErrorCode] = ChronoAuditHttpResults.Unavailable.Code;
            return false;
        }
    }

    private ApiMutationIntent Describe(HttpContext http)
    {
        var request = http.Request;
        var capture = http.Items.TryGetValue(ChronoAuditActivityMiddleware.CaptureItemKey, out var captured) ? captured as CapturingReadStream : null;
        var requestId = http.Items.TryGetValue(ChronoAuditHttpItems.RequestId, out var id) && id is Guid existing ? existing : Guid.NewGuid();
        http.Items[ChronoAuditHttpItems.RequestId] = requestId;

        var correlation = http.RequestServices.GetService<ICorrelationContext>();
        var sha = capture?.Sha256Hex();
        var user = http.User;
        return new ApiMutationIntent
        {
            RequestId = requestId,
            StartedAtUtc = clock.GetUtcNow(),
            UserId = options.FirstClaim(user, options.UserIdClaimTypes),
            ActAsUserId = user.FindFirst(options.ActAsUserIdClaimType)?.Value,
            SessionId = user.FindFirst(options.SessionIdClaimType)?.Value,
            CorrelationId = correlation is not null && correlation.CorrelationId != Guid.Empty ? correlation.CorrelationId : null,
            Method = request.Method,
            Path = request.Path.Value ?? string.Empty,
            EndpointKey = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            BodySha256 = string.IsNullOrEmpty(sha) ? EmptyBodySha256 : sha,
            BodyLength = capture?.TotalRead ?? 0,
        };
    }
}

/// <summary>
/// Records a mutation intent before every MVC action that writes (POST, PUT, PATCH, DELETE). When the intent cannot be stored the action
/// does not run and the answer is 503 with the code <c>ActivityLog.Unavailable</c>. The hash covers every byte the request has read when
/// the filter runs; MVC model binding has read a JSON or form body completely by then. The filter never reads the body itself.
/// </summary>
public sealed class MutationIntentFilter : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var writer = http.RequestServices.GetRequiredService<MutationIntentWriter>();
        if (writer.Applies(http) && !await writer.TryWriteAsync(http))
        {
            var error = ChronoAuditHttpResults.Unavailable;
            context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Title = error.Code, Detail = error.Message })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        await next();
    }
}

/// <summary>The same as <see cref="MutationIntentFilter"/> for minimal API endpoints.</summary>
public sealed class MutationIntentEndpointFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var writer = http.RequestServices.GetRequiredService<MutationIntentWriter>();
        if (writer.Applies(http) && !await writer.TryWriteAsync(http))
        {
            return ChronoAuditHttpResults.Problem(http, ChronoAuditHttpResults.Unavailable);
        }

        return await next(context);
    }
}

/// <summary>Registers and applies the mutation intent filters.</summary>
public static class ChronoAuditMutationIntentExtensions
{
    /// <summary>
    /// Records an intent before every MVC action that writes (a global filter) and makes the minimal API filter available. Needs
    /// <c>AddActivityLog</c>. For minimal APIs add the filter to a group with <see cref="WithChronoAuditMutationIntents{TBuilder}"/>, or to
    /// all endpoints with <c>app.MapGroup("").AddEndpointFilter&lt;MutationIntentEndpointFilter&gt;()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddChronoAuditMutationIntents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MutationIntentWriter>();
        services.Configure<MvcOptions>(options => options.Filters.Add<MutationIntentFilter>());
        return services;
    }

    /// <summary>Records a mutation intent before every write endpoint of the group or endpoint.</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder WithChronoAuditMutationIntents<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<TBuilder, MutationIntentEndpointFilter>();
    }
}
