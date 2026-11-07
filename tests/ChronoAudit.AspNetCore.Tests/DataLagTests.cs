using System.Net;
using ChronoAudit.AspNetCore;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChronoAudit.AspNetCore.Tests;

public sealed class DataLagTests
{
    private sealed class TestLagSource(DateTimeOffset? oldestWaitingAt) : IChronoAuditDataLagSource
    {
        public Task<DateTimeOffset?> OldestWaitingAtUtcAsync(CancellationToken cancellationToken)
            => Task.FromResult(oldestWaitingAt);
    }

    [Fact]
    public async Task TheHeader_CarriesTheLag_WhenASourceIsRegistered()
    {
        await using var rig = await HttpRig.StartAsync(services: s =>
        {
            s.AddSingleton<IChronoAuditDataLagSource>(new TestLagSource(HttpRig.T0.AddSeconds(-42)));
        });

        var response = await rig.GetAsync("/chronoaudit/history/tables", Who.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains(ChronoAuditHttpHeaders.DataLagSeconds));
        Assert.Equal("42", response.Headers.GetValues(ChronoAuditHttpHeaders.DataLagSeconds).Single());
    }

    [Fact]
    public async Task TheHeader_IsNotPresent_WhenThereIsNoLag_OrTheCallFails()
    {
        // When no source is registered, lag is null, so no header.
        await using var rig = await HttpRig.StartAsync();

        var okNoLag = await rig.GetAsync("/chronoaudit/history/tables", Who.Admin);
        Assert.Equal(HttpStatusCode.OK, okNoLag.StatusCode);
        Assert.False(okNoLag.Headers.Contains(ChronoAuditHttpHeaders.DataLagSeconds));

        // When source has a lag, but the response is an error (404), the header must not be set.
        await using var errorRig = await HttpRig.StartAsync(services: s =>
        {
            s.AddSingleton<IChronoAuditDataLagSource>(new TestLagSource(HttpRig.T0.AddSeconds(-42)));
        });

        var error = await errorRig.GetAsync($"/chronoaudit/saved-reports/{Guid.NewGuid()}", Who.Me());
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.False(error.Headers.Contains(ChronoAuditHttpHeaders.DataLagSeconds));
    }
}
