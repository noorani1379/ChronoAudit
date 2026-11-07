using System.Net;
using ChronoAudit.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class CorsTests
{
    [Fact]
    public async Task TheExposedHeaders_AreListed_AndTheSampleExposesThem()
    {
        Assert.Equal(
            new[] { "X-Data-Lag-Seconds", "X-Content-SHA256", "Content-Disposition" },
            ChronoAuditHttpHeaders.ExposedToBrowsers);

        await using var rig = await HttpRig.StartAsync(
            services: s => s.AddCors(),
            beforeAuth: app => app.UseCors(b => b.AllowAnyOrigin().WithExposedHeaders(ChronoAuditHttpHeaders.ExposedToBrowsers.ToArray())));

        var message = rig.Request(HttpMethod.Get, "/chronoaudit/history/tables", Who.Admin);
        message.Headers.Add("Origin", "https://app.example.com");

        var response = await rig.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Expose-Headers"));
        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers").FirstOrDefault();
        Assert.NotNull(exposed);
        foreach (var header in ChronoAuditHttpHeaders.ExposedToBrowsers)
        {
            Assert.Contains(header, exposed, StringComparison.OrdinalIgnoreCase);
        }
    }
}

