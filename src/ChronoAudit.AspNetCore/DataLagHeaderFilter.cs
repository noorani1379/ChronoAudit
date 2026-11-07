using System.Globalization;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore;

/// <summary>
/// Adds <c>X-Data-Lag-Seconds</c> to the answer of a read endpoint (a GET): how far behind the data is. The header is set only when the
/// answer is a success (2xx) and the lag is known; an error answer or an unknown lag has no header. A browser client reads it only when
/// the CORS policy of the host exposes it, see <see cref="ChronoAuditHttpHeaders.ExposedToBrowsers"/>. Needs <c>AddDataLag</c>.
/// </summary>
public sealed class DataLagHeaderFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) && http.RequestServices.GetService<IChronoAuditDataLag>() is { } dataLag)
        {
            var lag = await dataLag.GetLagSecondsAsync(http.RequestAborted);
            if (lag is { } seconds)
            {
                // The status is final only when the headers go out: decide then.
                http.Response.OnStarting(
                    state =>
                    {
                        var (response, value) = ((HttpResponse, int))state;
                        if (response.StatusCode is >= 200 and < 300)
                        {
                            response.Headers[ChronoAuditHttpHeaders.DataLagSeconds] = value.ToString(CultureInfo.InvariantCulture);
                        }

                        return Task.CompletedTask;
                    },
                    (http.Response, seconds));
            }
        }

        return await next(context);
    }
}
