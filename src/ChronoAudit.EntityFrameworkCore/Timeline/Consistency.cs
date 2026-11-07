using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>What one consistency check found: how many rows it expected and how many there are.</summary>
/// <param name="CheckName">The name of the check (a check that compares per category returns one result per category and names each).</param>
/// <param name="Expected">The count in the owning system.</param>
/// <param name="Actual">The count in ChronoAudit.</param>
public sealed record ConsistencyResult(string CheckName, long Expected, long Actual)
{
    /// <summary>Actual minus expected: negative means rows are missing in ChronoAudit, positive means orphans.</summary>
    public long Drift => Actual - Expected;

    /// <summary>True when the counts differ.</summary>
    public bool Drifts => Drift != 0;
}

/// <summary>A host check that compares what ChronoAudit holds with what the owning system holds. Register it in the container.</summary>
public interface IConsistencyCheck
{
    /// <summary>The name of the check, for logs.</summary>
    string Name { get; }

    /// <summary>Compares the two sides.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One result, or one per category when the check compares per category.</returns>
    Task<IReadOnlyList<ConsistencyResult>> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Settings of the consistency worker (section <c>ChronoAudit:Consistency</c>).</summary>
public sealed class ConsistencyOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Consistency";

    /// <summary>Hours between two runs (1 to 168). Default 6.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Seconds to wait before a drifting check is run again, so a difference that is only a delivery in flight can heal (0 to 3600). Default 60.</summary>
    public int GraceSeconds { get; set; } = 60;

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (IntervalHours is < 1 or > 168 || GraceSeconds is < 0 or > 3600)
        {
            throw new InvalidOperationException("ChronoAudit:Consistency: IntervalHours 1-168, GraceSeconds 0-3600.");
        }
    }
}

internal static class ConsistencyMetrics
{
    private static int _drifting;

    public static readonly Meter Meter = new(ChronoAuditMetrics.MeterName);

    public static readonly ObservableGauge<int> DriftingChecks = Meter.CreateObservableGauge(
        "chronoaudit.consistency.drifting_checks", () => Volatile.Read(ref _drifting), description: "Consistency checks that still drift after the grace wait.");

    public static void Set(int count) => Volatile.Write(ref _drifting, count);
}

/// <summary>
/// Runs the host's consistency checks every few hours. A check that drifts is run again after a grace wait; one that still drifts is
/// reported (a Warning with both counts and the signed drift, and the gauge). It never repairs anything. A failing check is logged and
/// the next one runs; a stopping host stops the worker.
/// </summary>
internal sealed class ConsistencyWorker(IServiceScopeFactory scopeFactory, ConsistencyOptions options, ILogger<ConsistencyWorker> logger) : BackgroundService
{
    internal TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(5);

    // A seam for the tests: the grace wait.
    internal Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = (span, token) => Task.Delay(span, token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Wait(InitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(options.IntervalHours));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One run over every check; returns the results that still drift. Internal for the tests.</summary>
    internal async Task<IReadOnlyList<ConsistencyResult>> RunOnceAsync(CancellationToken cancellationToken)
    {
        int count;
        using (var scope = scopeFactory.CreateScope())
        {
            count = scope.ServiceProvider.GetServices<IConsistencyCheck>().Count();
        }

        var first = new List<(int Index, ConsistencyResult Result)>();
        for (var index = 0; index < count; index++)
        {
            first.AddRange((await RunCheckAsync(index, cancellationToken)).Select(r => (index, r)));
        }

        var drifting = first.Where(x => x.Result.Drifts).ToList();
        if (drifting.Count > 0)
        {
            await Wait(TimeSpan.FromSeconds(options.GraceSeconds), cancellationToken);

            // Only the checks that drifted run again, and only the categories that drifted before count.
            var names = drifting.Select(x => (x.Index, x.Result.CheckName)).ToHashSet();
            var again = new List<ConsistencyResult>();
            foreach (var index in drifting.Select(x => x.Index).Distinct())
            {
                again.AddRange((await RunCheckAsync(index, cancellationToken)).Where(r => r.Drifts && names.Contains((index, r.CheckName))));
            }

            drifting = again.Select(r => (0, r)).ToList();
        }

        var still = drifting.Select(x => x.Result).ToList();
        ConsistencyMetrics.Set(still.Count);
        foreach (var result in still)
        {
            logger.LogWarning(
                "Consistency drift in {Check}: the owning system has {Expected}, ChronoAudit has {Actual} (drift {Drift}).",
                result.CheckName, result.Expected, result.Actual, result.Drift);
        }

        if (still.Count == 0)
        {
            logger.LogInformation("Consistency check: no drift.");
        }

        return still;
    }

    private async Task<IReadOnlyList<ConsistencyResult>> RunCheckAsync(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var check = scope.ServiceProvider.GetServices<IConsistencyCheck>().ElementAt(index);
            return await check.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A consistency check failed; the next one runs and this one runs again at the next interval.");
            return [];
        }
    }
}
