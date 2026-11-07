using System.Linq.Expressions;

namespace ChronoAudit;

/// <summary>The closed list of aggregations a measure can use.</summary>
public enum Aggregation
{
    /// <summary>The count.</summary>
    Count = 1,
    /// <summary>The count distinct.</summary>
    CountDistinct = 2,
    /// <summary>The sum.</summary>
    Sum = 3,
    /// <summary>The avg.</summary>
    Avg = 4,
    /// <summary>The min.</summary>
    Min = 5,
    /// <summary>The max.</summary>
    Max = 6
}

/// <summary>
/// A way to group rows. <see cref="Value"/> is the group key shown to the user (text); <see cref="Order"/>, when given, is the number the
/// groups are ordered by (a time grain: <c>year*10000 + month*100 + day</c>), and <see cref="Format"/> turns the key text into the label.
/// All three are expressions written in code: nothing a request says is ever turned into a column.
/// </summary>
public sealed record AnalyticsDimension<TRow>(
    string Key, string Title, Expression<Func<TRow, string?>> Value, Expression<Func<TRow, long?>>? Order = null, Func<string, string>? Format = null);

/// <summary>Factories of the dimensions that are not plain text.</summary>
public static class AnalyticsDimension
{
    /// <summary>
    /// A dimension grouped and ordered by a number (a calendar day, month, quarter or year): the key text is the number and
    /// <paramref name="format"/> (see <see cref="TimeGrain"/>) turns it into the label. A row whose number is null is one unknown group.
    /// </summary>
    public static AnalyticsDimension<TRow> Numeric<TRow>(string key, string title, Expression<Func<TRow, long?>> number, Func<string, string>? format = null)
    {
        var text = Expression.Lambda<Func<TRow, string?>>(Expression.Call(number.Body, typeof(long?).GetMethod(nameof(ToString), Type.EmptyTypes)!), number.Parameters);
        return new AnalyticsDimension<TRow>(key, title, text, number, format);
    }

    /// <summary>
    /// A dimension over an enum column (nullable or not): the key is the member NAME, written as a CASE over the members so it translates on
    /// every provider (no reliance on <c>Enum.ToString()</c> translation). A value that is not a member, and null, are one unknown group.
    /// </summary>
    public static AnalyticsDimension<TRow> EnumName<TRow, TProp>(string key, string title, Expression<Func<TRow, TProp>> column)
    {
        var enumType = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);
        if (!enumType.IsEnum)
        {
            throw new ArgumentException($"The column of '{key}' is not an enum.", nameof(column));
        }

        Expression body = Expression.Constant(null, typeof(string));
        foreach (var member in Enum.GetValues(enumType).Cast<object>().Reverse())
        {
            body = Expression.Condition(
                Expression.Equal(column.Body, Expression.Constant(member, typeof(TProp))),
                Expression.Constant(Enum.GetName(enumType, member), typeof(string)),
                body);
        }

        return new AnalyticsDimension<TRow>(key, title, Expression.Lambda<Func<TRow, string?>>(body, column.Parameters));
    }
}

/// <summary>What is computed per group. <see cref="Number"/> feeds Sum/Avg/Min/Max, <see cref="DistinctKey"/> feeds CountDistinct, Count needs neither.</summary>
public sealed record AnalyticsMeasure<TRow>(
    string Key, string Title, Aggregation Aggregation, Expression<Func<TRow, decimal?>>? Number = null, Expression<Func<TRow, string?>>? DistinctKey = null)
{
    /// <summary>The count.</summary>
    public static AnalyticsMeasure<TRow> Count(string key, string title) => new(key, title, Aggregation.Count);

    /// <summary>The count distinct.</summary>
    public static AnalyticsMeasure<TRow> CountDistinct(string key, string title, Expression<Func<TRow, string?>> distinctKey)
        => new(key, title, Aggregation.CountDistinct, DistinctKey: distinctKey);

    /// <summary>The of.</summary>
    public static AnalyticsMeasure<TRow> Of(string key, string title, Aggregation aggregation, Expression<Func<TRow, decimal?>> number)
        => aggregation is Aggregation.Count or Aggregation.CountDistinct
            ? throw new ArgumentException("Count and CountDistinct take no number.", nameof(aggregation))
            : new(key, title, aggregation, number);
}

/// <summary>
/// One analytics request. Everything is a catalog key or a typed value; the range is required and at most 36 months (the column it limits
/// is the subject's own). <see cref="TopN"/> is at most <see cref="AnalyticsLimits.MaxTopN"/>.
/// </summary>
public sealed record AnalyticsQuery(
    string Subject, IReadOnlyList<string> Dimensions, IReadOnlyList<string> Measures, IReadOnlyList<FilterNode>? Filters,
    DateTimeOffset From, DateTimeOffset To, int TopN = AnalyticsLimits.DefaultTopN, string? SortBy = null, bool SortDescending = true, bool ComparePrevious = false);

/// <summary>The analytics limits.</summary>
public static class AnalyticsLimits
{
    /// <summary>The max dimensions.</summary>
    public const int MaxDimensions = 3;
    /// <summary>The max measures.</summary>
    public const int MaxMeasures = 4;
    /// <summary>The default top n.</summary>
    public const int DefaultTopN = 100;
    /// <summary>The max top n.</summary>
    public const int MaxTopN = 500;
    /// <summary>The max months.</summary>
    public const int MaxMonths = 36;
}

/// <param name="Dimensions">The group key texts (the formatted label), in the order the request listed the dimensions; null for a row whose value is unknown.</param>
/// <param name="Measures">The values, in the order the request listed the measures.</param>
/// <param name="Previous">The same measures for the previous period of equal length (only with <c>ComparePrevious</c>; null when that group had no row then).</param>
public sealed record AnalyticsRow(IReadOnlyList<string?> Dimensions, IReadOnlyList<decimal?> Measures, IReadOnlyList<decimal?>? Previous = null);

/// <summary>A dimension or measure column of a result or catalog.</summary>
/// <param name="Key">The key of the column.</param>
/// <param name="Title">The title of the column.</param>
public sealed record AnalyticsColumn(string Key, string Title);

/// <param name="IsTruncated">True when more groups existed than <c>TopN</c> (the best by the chosen order are returned).</param>
/// <summary>The grouped result of one analytics query.</summary>
/// <param name="Dimensions">The dimension columns.</param>
/// <param name="Measures">The measure columns.</param>
/// <param name="Rows">The rows.</param>
/// <param name="From">The start of the range.</param>
/// <param name="To">The end of the range.</param>
/// <param name="PreviousFrom">The start of the previous period, when compared.</param>
/// <param name="PreviousTo">The end of the previous period, when compared.</param>
public sealed record AnalyticsResult(
    IReadOnlyList<AnalyticsColumn> Dimensions, IReadOnlyList<AnalyticsColumn> Measures, IReadOnlyList<AnalyticsRow> Rows, bool IsTruncated,
    DateTimeOffset From, DateTimeOffset To, DateTimeOffset? PreviousFrom = null, DateTimeOffset? PreviousTo = null);

/// <summary>The fixed row shape of one group as the database returns it: up to 3 dimension texts, their order numbers and up to 4 measure values.</summary>
public sealed class AnalyticsCell
{
    /// <summary>The s1.</summary>
    public string? S1 { get; set; }
    /// <summary>The s2.</summary>
    public string? S2 { get; set; }
    /// <summary>The s3.</summary>
    public string? S3 { get; set; }
    /// <summary>The n1.</summary>
    public long? N1 { get; set; }
    /// <summary>The n2.</summary>
    public long? N2 { get; set; }
    /// <summary>The n3.</summary>
    public long? N3 { get; set; }
    /// <summary>The m1.</summary>
    public decimal? M1 { get; set; }
    /// <summary>The m2.</summary>
    public decimal? M2 { get; set; }
    /// <summary>The m3.</summary>
    public decimal? M3 { get; set; }
    /// <summary>The m4.</summary>
    public decimal? M4 { get; set; }
}

/// <summary>The shape a source row is flattened into before grouping.</summary>
public sealed class AnalyticsSlot
{
    /// <summary>The s1.</summary>
    public string? S1 { get; set; }
    /// <summary>The s2.</summary>
    public string? S2 { get; set; }
    /// <summary>The s3.</summary>
    public string? S3 { get; set; }
    /// <summary>The n1.</summary>
    public long? N1 { get; set; }
    /// <summary>The n2.</summary>
    public long? N2 { get; set; }
    /// <summary>The n3.</summary>
    public long? N3 { get; set; }
    /// <summary>The v1.</summary>
    public decimal? V1 { get; set; }
    /// <summary>The v2.</summary>
    public decimal? V2 { get; set; }
    /// <summary>The v3.</summary>
    public decimal? V3 { get; set; }
    /// <summary>The v4.</summary>
    public decimal? V4 { get; set; }
    /// <summary>The k1.</summary>
    public string? K1 { get; set; }
    /// <summary>The k2.</summary>
    public string? K2 { get; set; }
    /// <summary>The k3.</summary>
    public string? K3 { get; set; }
    /// <summary>The k4.</summary>
    public string? K4 { get; set; }
}

/// <summary>The grouping key of a group. A record, so two rows with the same values are ONE group also when the query runs in memory.</summary>
public sealed record AnalyticsGroupKey
{
    /// <summary>The s1.</summary>
    public string? S1 { get; set; }
    /// <summary>The s2.</summary>
    public string? S2 { get; set; }
    /// <summary>The s3.</summary>
    public string? S3 { get; set; }
    /// <summary>The n1.</summary>
    public long? N1 { get; set; }
    /// <summary>The n2.</summary>
    public long? N2 { get; set; }
    /// <summary>The n3.</summary>
    public long? N3 { get; set; }
}

/// <summary>What the catalog endpoint tells the frontend about one subject.</summary>
public sealed record AnalyticsCatalogEntry(
    string Key, string Title, IReadOnlyList<AnalyticsColumn> Dimensions, IReadOnlyList<AnalyticsMeasureInfo> Measures, IReadOnlyList<AnalyticsFilterInfo> Filters);
/// <summary>A measure of a subject in the catalog.</summary>
/// <param name="Key">The key of the measure.</param>
/// <param name="Title">The title of the measure.</param>
/// <param name="Aggregation">How the measure is aggregated.</param>
/// <summary>The analytics measure info.</summary>
public sealed record AnalyticsMeasureInfo(string Key, string Title, Aggregation Aggregation);
/// <summary>A filter field of a subject in the catalog.</summary>
/// <param name="Key">The key of the filter field.</param>
/// <param name="Title">The title of the filter field.</param>
/// <param name="Operators">The operators the field accepts.</param>
/// <summary>The analytics filter info.</summary>
public sealed record AnalyticsFilterInfo(string Key, string Title, IReadOnlyList<FilterOp> Operators);

/// <summary>The analytics errors.</summary>
public static class AnalyticsErrors
{
    /// <summary>The range required.</summary>
    public static ChronoAuditError RangeRequired => ChronoAuditError.Validation("Analytics.RangeRequired", "A time range (from and to) is required for this report.");

    /// <summary>The range too wide.</summary>
    public static ChronoAuditError RangeTooWide => ChronoAuditError.Validation("Analytics.RangeTooWide", $"The time range of this report may be at most {AnalyticsLimits.MaxMonths} months.");

    /// <summary>The invalid request.</summary>
    public static ChronoAuditError InvalidRequest(string reason) => ChronoAuditError.Validation(FilterErrors.Code, $"The analytics request is not valid: {reason}");

    /// <summary>The unknown subject.</summary>
    public static ChronoAuditError UnknownSubject(string key) => ChronoAuditError.NotFound("Analytics.SubjectNotFound", $"The analytics subject '{key}' does not exist.");
}

/// <summary>The non-generic face of an analytics subject, so a registry can hold subjects over different row types.</summary>
public interface IAnalyticsSubject
{
    /// <summary>The stable key of the subject.</summary>
    string Key { get; }

    /// <summary>The title of the subject.</summary>
    string Title { get; }

    /// <summary>True when only the All scope may run the subject.</summary>
    bool RequiresAllScope { get; }

    /// <summary>The catalog entry: dimensions, measures and filters.</summary>
    /// <returns>The entry.</returns>
    AnalyticsCatalogEntry Describe();
}
