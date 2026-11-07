using System.Diagnostics.Metrics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>The settings of the API activity log (section <c>ChronoAudit:ActivityLog</c>).</summary>
public sealed class ActivityLogOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:ActivityLog";

    /// <summary>Request paths that start with one of these are not logged.</summary>
    public IList<string> ExcludedPathPrefixes { get; } = new List<string> { "/health", "/version", "/swagger", "/hubs" };

    /// <summary>Seconds between two rounds of the activity hash worker (at least 1).</summary>
    public int HashIntervalSeconds { get; set; } = 5;

    /// <summary>Rows per hash transaction (at least 1).</summary>
    public int HashBatchSize { get; set; } = 500;

    /// <summary>A mutation intent without a result row older than this many minutes is reported as incomplete (at least 1).</summary>
    public int IncompleteIntentAgeMinutes { get; set; } = 5;

    /// <summary>Checks the values.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (HashIntervalSeconds < 1 || HashBatchSize < 1 || IncompleteIntentAgeMinutes < 1)
        {
            throw new InvalidOperationException("ChronoAudit:ActivityLog: HashIntervalSeconds, HashBatchSize and IncompleteIntentAgeMinutes must be at least 1.");
        }
    }
}

/// <summary>The calendar used for the calendar columns and time grains (section <c>ChronoAudit:Calendar</c>).</summary>
public sealed class CalendarOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "ChronoAudit:Calendar";

    /// <summary><c>Gregorian</c> or <c>Persian</c>.</summary>
    public string Kind { get; set; } = "Gregorian";

    /// <summary>The time zone id whose day boundaries the calendar uses (default <c>UTC</c>).</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Builds the calendar.</summary>
    /// <returns>The calendar.</returns>
    /// <exception cref="InvalidOperationException">The kind or the time zone is unknown.</exception>
    public IChronoAuditCalendar Create()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"ChronoAudit:Calendar:TimeZoneId '{TimeZoneId}' is not a known time zone.", ex);
        }

        return Kind switch
        {
            "Gregorian" => new GregorianChronoAuditCalendar(zone),
            "Persian" => new PersianChronoAuditCalendar(zone),
            _ => throw new InvalidOperationException($"ChronoAudit:Calendar:Kind '{Kind}' is not known; use Gregorian or Persian."),
        };
    }
}

internal static class ActivityMetrics
{
    public static readonly Meter Meter = new(ChronoAuditMetrics.MeterName);

    public static readonly Counter<long> Retries = Meter.CreateCounter<long>("chronoaudit.activity.retries", description: "Retried writes of the activity log.");

    public static readonly Counter<long> WriteFailures = Meter.CreateCounter<long>("chronoaudit.activity.write_failures", description: "Activity log entries that were not stored and went to the fallback log.");
}

/// <summary>Writes request log rows to the store context in a unit of work of its own.</summary>
internal sealed class EfApiRequestLogStore(ChronoAuditStoreRegistration registration, IServiceScopeFactory scopeFactory, IChronoAuditCalendar calendar) : IApiRequestLogStore
{
    public async Task WriteAsync(ApiRequestLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        var at = entry.OccurredAtUtc.ToUniversalTime();
        var parts = calendar.PartsOf(at);

        db.Set<ApiRequestLog>().Add(new ApiRequestLog
        {
            RequestId = entry.RequestId,
            OccurredAtUtc = at,
            DurationMs = entry.DurationMs,
            UserId = entry.UserId,
            ActAsUserId = entry.ActAsUserId,
            SessionId = entry.SessionId,
            CorrelationId = entry.CorrelationId,
            CalendarYear = (short)parts.Year,
            CalendarMonth = (short)parts.Month,
            CalendarDay = (short)parts.Day,
            CalendarQuarter = (short)parts.Quarter,
            HttpMethod = entry.HttpMethod,
            Path = entry.Path,
            EndpointKey = entry.EndpointKey,
            QueryString = entry.QueryString,
            StatusCode = entry.StatusCode,
            ErrorCode = entry.ErrorCode,
            IpAddress = entry.IpAddress,
            UserAgent = entry.UserAgent,
            HasBody = entry.HasBody,
            Body = entry.HasStoredBody
                ? new ApiRequestBody { ContentType = entry.BodyContentType, Body = entry.Body, IsTruncated = entry.BodyIsTruncated, FileMetadata = entry.FileMetadata }
                : null,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ChronoAuditDialect.For(db).IsUniqueViolation(ex))
        {
            // A retry of a write that already succeeded: one row is stored, and that is what was wanted.
        }
    }
}

/// <summary>Writes intents to the store context. A failure reaches the caller (fail-closed).</summary>
internal sealed class EfApiMutationIntentStore(ChronoAuditStoreRegistration registration, IServiceScopeFactory scopeFactory) : IApiMutationIntentStore
{
    public async Task WriteAsync(ApiMutationIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        using var scope = scopeFactory.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
        db.Set<ApiMutationIntent>().Add(intent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ChronoAuditDialect.For(db).IsUniqueViolation(ex))
        {
            // The same request again (a retry): one intent is stored.
        }
    }
}

/// <summary>
/// The write policy of the activity log: fail-open, three tries, then a fallback log line. A try that fails is repeated after a short,
/// jittered pause (the write is idempotent on <c>RequestId</c>, so repeating it can never store two rows). When the three tries fail, the
/// masked entry is written as one JSON line to the logger <c>ChronoAudit.ActivityLog.Fallback</c> (so the evidence can be re-imported),
/// a counter is raised and the failure is logged at <c>Critical</c> the first time. After a failure the store stops trying the database for
/// a short time (a circuit breaker): during an outage every call would otherwise wait for its own retries. It never throws: a lost log
/// row must not become a failed business call.
/// </summary>
public sealed class ResilientApiRequestLogStore : IApiRequestLogStore
{
    internal const int MaxTries = 3;
    internal const string FallbackMarker = "API_ACTIVITY_LOG_FALLBACK";
    internal const string FallbackCategory = "ChronoAudit.ActivityLog.Fallback";

    internal static readonly TimeSpan BreakerOpenFor = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan[] PausesBeforeTry = [TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400)];
    private static readonly JsonSerializerOptions FallbackJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IApiRequestLogStore _inner;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, Task> _pause;

    private long _openUntilTicks;
    private int _alerted;

    /// <summary>Creates the store around an inner store.</summary>
    /// <param name="inner">The store that writes to the database.</param>
    /// <param name="loggerFactory">Creates the fallback logger.</param>
    /// <param name="clock">The time source.</param>
    public ResilientApiRequestLogStore(IApiRequestLogStore inner, ILoggerFactory loggerFactory, TimeProvider clock)
        : this(inner, loggerFactory, clock, pause => Task.Delay(pause))
    {
    }

    internal ResilientApiRequestLogStore(IApiRequestLogStore inner, ILoggerFactory loggerFactory, TimeProvider clock, Func<TimeSpan, Task> pause)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _inner = inner;
        _logger = loggerFactory.CreateLogger(FallbackCategory);
        _clock = clock;
        _pause = pause;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ApiRequestLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Volatile.Read(ref _openUntilTicks) > _clock.GetUtcNow().UtcTicks)
        {
            WriteFallback(entry, "circuit_open", exception: null);
            return;
        }

        Exception? last = null;
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    ActivityMetrics.Retries.Add(1);
                    await _pause(Jitter(PausesBeforeTry[attempt]));
                }

                await _inner.WriteAsync(entry, cancellationToken);
                Interlocked.Exchange(ref _alerted, 0);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        Volatile.Write(ref _openUntilTicks, (_clock.GetUtcNow() + BreakerOpenFor).UtcTicks);
        WriteFallback(entry, "retries_exhausted", last);
    }

    private static TimeSpan Jitter(TimeSpan pause)
        => pause == TimeSpan.Zero ? pause : pause * (0.75 + (Random.Shared.NextDouble() * 0.5));

    private void WriteFallback(ApiRequestLogEntry entry, string reason, Exception? exception)
    {
        ActivityMetrics.WriteFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

        try
        {
            // The first failure since the last success is an alert; the rest of an outage is counted and written, not repeated as alerts.
            var level = Interlocked.Exchange(ref _alerted, 1) == 0 ? LogLevel.Critical : LogLevel.Error;
            _logger.Log(
                level,
                exception,
                FallbackMarker + " {Reason} {RequestId} {EntryJson}",
                reason,
                entry.RequestId,
                JsonSerializer.Serialize(entry, FallbackJson));
        }
        catch (Exception ex)
        {
            // The logger itself failed: nothing is left to do, and the call must not fail.
            _logger.LogError(ex, "The API activity fallback line for {RequestId} could not be written.", entry.RequestId);
        }
    }
}
