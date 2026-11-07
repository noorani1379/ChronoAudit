using System.Diagnostics.Metrics;
using ChronoAudit.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore.Tests;

public sealed class DataLagTests
{
    private static readonly DateTimeOffset T0 = TimelineRig.T0;

    private sealed class Source(Func<int, DateTimeOffset?> answer) : IChronoAuditDataLagSource
    {
        public int Calls { get; private set; }

        public Task<DateTimeOffset?> OldestWaitingAtUtcAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(Calls));
        }
    }

    private static async Task<ActivityHost> HostAsync(params IChronoAuditDataLagSource[] sources)
    {
        var host = await TimelineRig.HostAsync(b =>
        {
            b.AddDataLag();
            foreach (var source in sources)
            {
                b.Services.AddSingleton(source);
            }
        });
        host.Clock.Set(T0);
        return host;
    }

    private static IChronoAuditDataLag Lag(ActivityHost host) => host.Provider.GetRequiredService<IChronoAuditDataLag>();

    [Fact]
    public async Task NoSourceRegistered_MeansNoLag()
    {
        await using var host = await HostAsync();

        Assert.Null(await Lag(host).GetLagSecondsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheAnswer_IsRememberedForAFewSeconds_ThenReadAgain()
    {
        var source = new Source(call => T0.AddSeconds(-10 * call));
        await using var host = await HostAsync(source);

        var first = await Lag(host).GetLagSecondsAsync(CancellationToken.None);
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        var remembered = await Lag(host).GetLagSecondsAsync(CancellationToken.None);
        host.Clock.Advance(TimeSpan.FromSeconds(2));
        var again = await Lag(host).GetLagSecondsAsync(CancellationToken.None);

        Assert.Equal((10, 10, 24), (first, remembered, again));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task TheOldestOfSeveralSources_Wins()
    {
        await using var host = await HostAsync(new Source(_ => T0.AddSeconds(-5)), new Source(_ => T0.AddSeconds(-90)), new Source(_ => null));

        Assert.Equal(90, await Lag(host).GetLagSecondsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFailingSource_GivesNoLag_AndNeverThrows_WhileAWorkingOneStillCounts()
    {
        var failing = new Source(_ => throw new InvalidOperationException("outbox down"));
        await using var onlyFailing = await HostAsync(failing);
        await using var mixed = await HostAsync(new Source(_ => throw new InvalidOperationException("down")), new Source(_ => T0.AddSeconds(-7)));

        Assert.Null(await Lag(onlyFailing).GetLagSecondsAsync(CancellationToken.None));
        Assert.Equal(7, await Lag(mixed).GetLagSecondsAsync(CancellationToken.None));
        Assert.Contains(onlyFailing.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledRead_StillCancels()
    {
        await using var host = await HostAsync(new CancellingSource());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Lag(host).GetLagSecondsAsync(CancellationToken.None));
    }

    private sealed class CancellingSource : IChronoAuditDataLagSource
    {
        public Task<DateTimeOffset?> OldestWaitingAtUtcAsync(CancellationToken cancellationToken) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task NothingWaiting_IsZero_AndAMessageFromTheFuture_NeverGoesNegative()
    {
        await using var none = await HostAsync(new Source(_ => null));
        await using var future = await HostAsync(new Source(_ => T0.AddMinutes(5)));

        Assert.Equal(0, await Lag(none).GetLagSecondsAsync(CancellationToken.None));
        Assert.Equal(0, await Lag(future).GetLagSecondsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACacheOfZeroSeconds_AsksTheSourcesEveryTime()
    {
        var source = new Source(_ => T0);
        await using var host = await TimelineRig.HostAsync(b =>
        {
            b.AddDataLag();
            b.Services.AddSingleton<IChronoAuditDataLagSource>(source);
        }, configure: o => o.DataLag.CacheSeconds = 0);

        await Lag(host).GetLagSecondsAsync(CancellationToken.None);
        await Lag(host).GetLagSecondsAsync(CancellationToken.None);

        Assert.Equal(2, source.Calls);
        Assert.Throws<InvalidOperationException>(new DataLagOptions { CacheSeconds = 61 }.Validate);
    }
}

public sealed class ConsistencyWorkerTests
{
    private sealed class ScriptedCheck(string name, Func<int, IReadOnlyList<ConsistencyResult>> script) : IConsistencyCheck
    {
        public int Calls { get; private set; }

        public string Name => name;

        public Task<IReadOnlyList<ConsistencyResult>> RunAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(script(Calls));
        }
    }

    private static async Task<(ActivityHost Host, ConsistencyWorker Worker, int[] Waits)> RigAsync(params IConsistencyCheck[] checks)
    {
        var host = await TimelineRig.HostAsync(b =>
        {
            foreach (var check in checks)
            {
                b.Services.AddSingleton(check);
            }
        });
        var waits = new int[1];
        var worker = new ConsistencyWorker(
            host.Provider.GetRequiredService<IServiceScopeFactory>(), new ConsistencyOptions { GraceSeconds = 30 }, host.Provider.GetRequiredService<ILogger<ConsistencyWorker>>())
        {
            Wait = (_, _) =>
            {
                waits[0]++;
                return Task.CompletedTask;
            },
        };
        return (host, worker, waits);
    }

    private static IReadOnlyList<ConsistencyResult> One(string name, long expected, long actual) => [new ConsistencyResult(name, expected, actual)];

    [Fact]
    public void ShortChronoAuditMeansMissingRows_ExtraMeansOrphans_AndTheSignShowsWhich()
    {
        var missing = new ConsistencyResult("orders", Expected: 10, Actual: 7);
        var orphans = new ConsistencyResult("orders", Expected: 10, Actual: 12);
        var same = new ConsistencyResult("orders", Expected: 10, Actual: 10);

        Assert.Equal((-3L, true), (missing.Drift, missing.Drifts));
        Assert.Equal((2L, true), (orphans.Drift, orphans.Drifts));
        Assert.False(same.Drifts);
    }

    [Fact]
    public async Task TheWorker_ReportsADriftThatOutlivesTheGrace_WithBothCountsAndTheSign_AndNeverRepairs()
    {
        var check = new ScriptedCheck("orders", _ => One("orders", 10, 7));
        var (host, worker, waits) = await RigAsync(check);
        await using var _ = host;
        var gauge = Observe();

        var drifting = await worker.RunOnceAsync(CancellationToken.None);

        var result = Assert.Single(drifting);
        Assert.Equal(("orders", -3L), (result.CheckName, result.Drift));
        Assert.Equal(1, waits[0]);
        Assert.Equal(2, check.Calls);
        var warning = Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("orders", warning.Message, StringComparison.Ordinal);
        Assert.Contains("10", warning.Message, StringComparison.Ordinal);
        Assert.Contains("7", warning.Message, StringComparison.Ordinal);
        Assert.Contains("-3", warning.Message, StringComparison.Ordinal);
        Assert.Equal(1, gauge());
    }

    [Fact]
    public async Task TheWorker_IgnoresADifferenceThatHealedDuringTheGrace()
    {
        var check = new ScriptedCheck("orders", call => call == 1 ? One("orders", 10, 9) : One("orders", 10, 10));
        var (host, worker, _) = await RigAsync(check);
        await using var _ = host;
        var gauge = Observe();

        var drifting = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Empty(drifting);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(0, gauge());
    }

    [Fact]
    public async Task ACleanRun_DoesNotWait_AndNeverRunsACheckTwice()
    {
        var check = new ScriptedCheck("orders", _ => One("orders", 5, 5));
        var (host, worker, waits) = await RigAsync(check);
        await using var _ = host;

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal((0, 1), (waits[0], check.Calls));
    }

    [Fact]
    public async Task AStatusOnlyOneSideKnows_IsItsOwnDriftingCheck()
    {
        var check = new ScriptedCheck("by-status", _ => [new("by-status:Paid", 4, 4), new("by-status:Refunded", 0, 2), new("by-status:Open", 3, 0)]);
        var (host, worker, _) = await RigAsync(check);
        await using var _ = host;

        var drifting = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { ("by-status:Open", -3L), ("by-status:Refunded", 2L) }, drifting.OrderBy(r => r.CheckName).Select(r => (r.CheckName, r.Drift)));
    }

    [Fact]
    public async Task AFailingCheck_IsLogged_AndTheNextCheckStillRuns()
    {
        var broken = new ScriptedCheck("broken", _ => throw new InvalidOperationException("source offline"));
        var next = new ScriptedCheck("next", _ => One("next", 1, 0));
        var (host, worker, _) = await RigAsync(broken, next);
        await using var _ = host;

        var drifting = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal("next", Assert.Single(drifting).CheckName);
        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("consistency check failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACancelledRun_StillCancels()
    {
        var check = new ScriptedCheck("orders", _ => One("orders", 1, 1));
        var (host, worker, _) = await RigAsync(check);
        await using var _ = host;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(cts.Token));

        Assert.Equal(0, check.Calls);
    }

    [Fact]
    public void TheOptions_RefuseValuesOutOfRange()
    {
        Assert.Throws<InvalidOperationException>(new ConsistencyOptions { IntervalHours = 0 }.Validate);
        Assert.Throws<InvalidOperationException>(new ConsistencyOptions { GraceSeconds = 4000 }.Validate);
        new ConsistencyOptions().Validate();
    }

    // Reads the observable gauge the way a metrics collector would.
    private static Func<int> Observe()
    {
        var value = 0;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "chronoaudit.consistency.drifting_checks")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, measurement, _, _) => value = measurement);
        listener.Start();
        return () =>
        {
            listener.RecordObservableInstruments();
            return value;
        };
    }
}
