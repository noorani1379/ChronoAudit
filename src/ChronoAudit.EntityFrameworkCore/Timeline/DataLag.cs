using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>Settings of the data lag indicator (section <c>ChronoAudit:DataLag</c>).</summary>
public sealed class DataLagOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:DataLag";

    /// <summary>How many seconds an answer is remembered (0 to 60; 0 asks the sources every time). Default 3.</summary>
    public int CacheSeconds { get; set; } = 3;

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (CacheSeconds is < 0 or > 60)
        {
            throw new InvalidOperationException("ChronoAudit:DataLag:CacheSeconds must be between 0 and 60.");
        }
    }
}

/// <summary>
/// Tells ChronoAudit how old the oldest piece of data is that the host has not yet delivered into it (for example the oldest outbox
/// message that will become a timeline event). Only the host knows that. Register zero or more.
/// </summary>
public interface IChronoAuditDataLagSource
{
    /// <summary>When the oldest undelivered item was created.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>That time, or null when nothing is waiting.</returns>
    Task<DateTimeOffset?> OldestWaitingAtUtcAsync(CancellationToken cancellationToken);
}

/// <summary>How far behind the data is, for a "data as of" notice on read endpoints.</summary>
public interface IChronoAuditDataLag
{
    /// <summary>The lag in whole seconds.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Null when no source is registered or every source failed; 0 when nothing is waiting; otherwise the age of the oldest waiting item (never negative).</returns>
    Task<int?> GetLagSecondsAsync(CancellationToken cancellationToken);
}

internal sealed class DataLagReader(IServiceScopeFactory scopeFactory, DataLagOptions options, TimeProvider clock, ILogger<DataLagReader> logger) : IChronoAuditDataLag
{
    // The remembered answer (also a null one) and when it stops being valid; replaced as a whole, so a reader never sees half of it.
    private sealed record Remembered(int? Lag, DateTimeOffset Until);

    private volatile Remembered? _remembered;

    public async Task<int?> GetLagSecondsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_remembered is { } remembered && now < remembered.Until)
        {
            return remembered.Lag;
        }

        var lag = await ReadAsync(now, cancellationToken);
        _remembered = new Remembered(lag, clock.GetUtcNow().AddSeconds(options.CacheSeconds));
        return lag;
    }

    private async Task<int?> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IChronoAuditDataLagSource>().ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        DateTimeOffset? oldest = null;
        var answered = 0;
        foreach (var source in sources)
        {
            try
            {
                var at = await source.OldestWaitingAtUtcAsync(cancellationToken);
                answered++;
                if (at is { } value && (oldest is null || value < oldest))
                {
                    oldest = value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The data lag source {Source} could not be read and is ignored.", source.GetType().Name);
            }
        }

        if (answered == 0)
        {
            return null;
        }

        return oldest is null ? 0 : (int)Math.Min(int.MaxValue, Math.Max(0, Math.Floor((now - oldest.Value).TotalSeconds)));
    }
}
