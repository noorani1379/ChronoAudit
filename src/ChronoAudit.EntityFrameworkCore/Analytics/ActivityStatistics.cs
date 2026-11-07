using System.Globalization;

namespace ChronoAudit.EntityFrameworkCore;

/// <summary>One bucket of the activity statistics.</summary>
/// <param name="Key">The bucket key: a day or month label, an endpoint, a status class such as <c>4xx</c>, or a user.</param>
/// <param name="Count">The number of API calls in the bucket.</param>
public sealed record ActivityBucket(string Key, long Count);

/// <summary>The API call counts of a range, grouped by one dimension.</summary>
/// <param name="Buckets">The buckets: days and months oldest first, the others biggest first.</param>
/// <param name="Total">The number of calls in the whole range (also when the buckets were capped).</param>
/// <param name="IsCapped">True when more buckets existed than are returned.</param>
public sealed record ActivityStatistics(IReadOnlyList<ActivityBucket> Buckets, long Total, bool IsCapped);

/// <summary>Counts API calls by day, month, endpoint, status class or user.</summary>
public interface IActivityStatistics
{
    /// <summary>Counts the calls of a range (both ends inclusive, at most 366 days).</summary>
    /// <param name="from">The start of the range.</param>
    /// <param name="to">The end of the range.</param>
    /// <param name="by">One of <c>day</c>, <c>month</c>, <c>endpoint</c>, <c>statusClass</c>, <c>user</c> (the last one needs the All scope).</param>
    /// <param name="scope">The caller's data scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The statistics, or <c>Filter.NotAllowed</c>, <c>Analytics.RangeTooWide</c>, <c>Analytics.SubjectForbidden</c>.</returns>
    Task<ChronoAuditResult<ActivityStatistics>> CountAsync(DateTimeOffset from, DateTimeOffset to, string by, ChronoAuditScope scope, CancellationToken cancellationToken);
}

internal sealed class ActivityStatisticsService(IAnalyticsService analytics) : IActivityStatistics
{
    private const int TopN = AnalyticsLimits.MaxTopN;

    public async Task<ChronoAuditResult<ActivityStatistics>> CountAsync(
        DateTimeOffset from, DateTimeOffset to, string by, ChronoAuditScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (by is not ("day" or "month" or "endpoint" or "statusClass" or "user"))
        {
            return FilterErrors.NotAllowed(by ?? string.Empty);
        }

        if (to < from)
        {
            return ChronoAuditError.Validation("ChronoAudit.Validation", "The end of the range must not be before its start.");
        }

        if (to - from > TimeSpan.FromDays(366))
        {
            return ChronoAuditError.Validation("Analytics.RangeTooWide", "The range may be at most 366 days.");
        }

        if (by == "user" && scope.Kind != ScopeKind.All)
        {
            return ChronoAuditError.Forbidden("Analytics.SubjectForbidden", "Counting by user needs the All scope.");
        }

        var chronological = by is "day" or "month";
        var query = new AnalyticsQuery(
            ChronoAuditAnalyticsSubjects.ActivityKey, [by], ["count"], null, from, to, TopN, chronological ? by : "count", !chronological, false);
        var result = await analytics.RunAsync(query, scope, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        var buckets = result.Value.Rows
            .Select(r => new ActivityBucket(KeyOf(by, r.Dimensions[0]), (long)(r.Measures[0] ?? 0m)))
            .ToList();
        if (!chronological)
        {
            buckets = buckets.OrderByDescending(b => b.Count).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();
        }

        var total = buckets.Sum(b => b.Count);
        if (result.Value.IsTruncated)
        {
            // The buckets were cut: the whole count comes from a second statement grouped by year (a handful of groups).
            var all = await analytics.RunAsync(query with { Dimensions = ["year"], SortBy = "year", SortDescending = false }, scope, cancellationToken);
            if (all.IsSuccess)
            {
                total = all.Value.Rows.Sum(r => (long)(r.Measures[0] ?? 0m));
            }
        }

        return new ActivityStatistics(buckets, total, result.Value.IsTruncated);
    }

    private static string KeyOf(string by, string? label) => by switch
    {
        "user" => label ?? "(unknown)",
        "endpoint" => label ?? "(none)",
        "statusClass" => label is { Length: 3 } && int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? $"{n / 100}xx" : label ?? "(none)",
        _ => label ?? "(none)",
    };
}
