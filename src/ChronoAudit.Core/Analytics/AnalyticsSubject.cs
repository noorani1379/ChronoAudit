using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace ChronoAudit;

/// <summary>
/// What running a request needs: the group query (one statement) and how to turn its rows into the result. The caller materializes
/// <see cref="Cells"/> (and <see cref="PreviousCells"/> for a comparison) with its own provider; this library has no data access.
/// </summary>
public sealed class AnalyticsPlan
{
    /// <summary>The cells.</summary>
    public required IQueryable<AnalyticsCell> Cells { get; init; }
    /// <summary>The previous cells.</summary>
    public IQueryable<AnalyticsCell>? PreviousCells { get; init; }

    /// <summary>Rows fetched: <c>TopN + 1</c>, the extra one only tells whether the result was cut.</summary>
    public required int Take { get; init; }

    /// <summary>The shape.</summary>
    public required Func<IReadOnlyList<AnalyticsCell>, IReadOnlyList<AnalyticsCell>?, AnalyticsResult> Shape { get; init; }
}

/// <summary>
/// One thing to analyse (dossiers, revenue, ...): a closed catalog of dimensions, measures and filter fields over one row type, and the
/// column the required time range limits. <see cref="Plan"/> turns a request into ONE group query: rows are flattened into a fixed slot
/// shape, grouped by up to three keys and aggregated by up to four measures, in a single statement (twin: <c>StatisticsReader.Compose</c>,
/// generalized). A key that is not in the catalog is a typed refusal, never a column name.
/// </summary>
public sealed class AnalyticsSubject<TRow> : IAnalyticsSubject
{
    private readonly Expression<Func<TRow, DateTimeOffset>> _range;
    private readonly IReadOnlyDictionary<string, AnalyticsDimension<TRow>> _dimensions;
    private readonly IReadOnlyDictionary<string, AnalyticsMeasure<TRow>> _measures;
    private readonly IReadOnlyList<IFilterField<TRow>> _filterFields;
    private readonly FilterBuilder<TRow> _filters;
    /// <summary>The analytics subject.</summary>
    public AnalyticsSubject(
        string key, string title, Expression<Func<TRow, DateTimeOffset>> range, IEnumerable<AnalyticsDimension<TRow>> dimensions,
        IEnumerable<AnalyticsMeasure<TRow>> measures, IEnumerable<IFilterField<TRow>> filters, FilterLimits? limits = null, ITextNormalizer? normalizer = null)
    {
        Key = key;
        Title = title;
        _range = range;
        _dimensions = dimensions.ToDictionary(d => d.Key, StringComparer.Ordinal);
        _measures = measures.ToDictionary(m => m.Key, StringComparer.Ordinal);
        _filterFields = filters.ToList();
        _filters = new FilterBuilder<TRow>(_filterFields, limits, normalizer);
        foreach (var measure in _measures.Values)
        {
            var valid = measure.Aggregation switch
            {
                Aggregation.Count => true,
                Aggregation.CountDistinct => measure.DistinctKey is not null,
                _ => measure.Number is not null
            };
            if (!valid)
            {
                throw new ArgumentException($"The measure '{measure.Key}' of subject '{key}' lacks the expression its aggregation needs.", nameof(measures));
            }
        }
    }

    /// <summary>The key.</summary>
    public string Key { get; }

    /// <summary>The title.</summary>
    public string Title { get; }

    /// <summary>True when only the All scope may run this subject.</summary>
    public bool RequiresAllScope { get; init; }

    /// <summary>The describe.</summary>
    public AnalyticsCatalogEntry Describe() => new(
        Key, Title,
        _dimensions.Values.Select(d => new AnalyticsColumn(d.Key, d.Title)).ToList(),
        _measures.Values.Select(m => new AnalyticsMeasureInfo(m.Key, m.Title, m.Aggregation)).ToList(),
        _filterFields.Select(f => new AnalyticsFilterInfo(f.Key, f.Title, f.Operators.OrderBy(o => o).ToList())).ToList());

    /// <summary>The plan.</summary>
    public ChronoAuditResult<AnalyticsPlan> Plan(AnalyticsQuery q, IQueryable<TRow> scoped)
    {
        // ---- the request, checked against the catalog and the limits before anything is built --------------------------------------
        if (q.Dimensions is not { Count: >= 1 and <= AnalyticsLimits.MaxDimensions } || q.Dimensions.Distinct(StringComparer.Ordinal).Count() != q.Dimensions.Count)
        {
            return Fail($"The number of dimensions must be between 1 and {AnalyticsLimits.MaxDimensions}, without repeats.");
        }

        if (q.Measures is not { Count: >= 1 and <= AnalyticsLimits.MaxMeasures } || q.Measures.Distinct(StringComparer.Ordinal).Count() != q.Measures.Count)
        {
            return Fail($"The number of measures must be between 1 and {AnalyticsLimits.MaxMeasures}, without repeats.");
        }

        if (q.TopN is < 1 or > AnalyticsLimits.MaxTopN)
        {
            return Fail($"The number of rows (TopN) must be between 1 and {AnalyticsLimits.MaxTopN}.");
        }

        if (q.From > q.To)
        {
            return Fail("The time range is not valid: 'from' must not be after 'to'.");
        }

        if (q.From.AddMonths(AnalyticsLimits.MaxMonths) < q.To)
        {
            return ChronoAuditResult.Failure<AnalyticsPlan>(AnalyticsErrors.RangeTooWide);
        }

        var dims = new List<AnalyticsDimension<TRow>>();
        foreach (var key in q.Dimensions)
        {
            if (!_dimensions.TryGetValue(key, out var d))
            {
                return ChronoAuditResult.Failure<AnalyticsPlan>(FilterErrors.NotAllowed(key));
            }

            dims.Add(d);
        }

        var measures = new List<AnalyticsMeasure<TRow>>();
        foreach (var key in q.Measures)
        {
            if (!_measures.TryGetValue(key, out var m))
            {
                return ChronoAuditResult.Failure<AnalyticsPlan>(FilterErrors.NotAllowed(key));
            }

            measures.Add(m);
        }

        var sortIndex = -1;
        var sortIsMeasure = true;
        if (q.SortBy is not null)
        {
            sortIndex = q.Measures.ToList().FindIndex(k => k == q.SortBy);
            if (sortIndex < 0)
            {
                sortIndex = q.Dimensions.ToList().FindIndex(k => k == q.SortBy);
                sortIsMeasure = false;
            }

            if (sortIndex < 0)
            {
                return ChronoAuditResult.Failure<AnalyticsPlan>(FilterErrors.NotAllowed(q.SortBy));
            }
        }
        else
        {
            sortIndex = 0; // default: the first measure
        }

        var filter = _filters.Build(q.Filters);
        if (filter.IsFailure)
        {
            return ChronoAuditResult.Failure<AnalyticsPlan>(filter.Error);
        }

        // ---- the group queries -------------------------------------------------------------------------------------------------------
        IQueryable<AnalyticsCell> Build(DateTimeOffset from, DateTimeOffset to)
        {
            var source = scoped.Where(InRange(from, to)).Where(filter.Value);
            return AnalyticsEngine.Group(source, dims, measures, sortIsMeasure, sortIndex, q.SortDescending, q.TopN + 1);
        }

        var span = q.To - q.From;
        var previousTo = q.From.AddTicks(-1);
        var previousFrom = previousTo - span;

        return ChronoAuditResult.Success(new AnalyticsPlan
        {
            Cells = Build(q.From, q.To),
            PreviousCells = q.ComparePrevious ? Build(previousFrom, previousTo) : null,
            Take = q.TopN + 1,
            Shape = (cells, previous) => AnalyticsEngine.Shape(
                cells, previous, dims.Select(d => (d.Key, d.Title, d.Format)).ToList(), measures.Select(m => (m.Key, m.Title)).ToList(), q,
                q.ComparePrevious ? previousFrom : null, q.ComparePrevious ? previousTo : null)
        });
    }

    private static ChronoAuditResult<AnalyticsPlan> Fail(string reason) => ChronoAuditResult.Failure<AnalyticsPlan>(AnalyticsErrors.InvalidRequest(reason));

    private Expression<Func<TRow, bool>> InRange(DateTimeOffset from, DateTimeOffset to)
    {
        // The bounds are read from a property of a constant holder, so EF sends them as parameters (one plan per shape).
        var low = Expression.Property(Expression.Constant(new Holder<DateTimeOffset>(from)), nameof(Holder<DateTimeOffset>.Value));
        var high = Expression.Property(Expression.Constant(new Holder<DateTimeOffset>(to)), nameof(Holder<DateTimeOffset>.Value));
        var body = Expression.AndAlso(Expression.GreaterThanOrEqual(_range.Body, low), Expression.LessThanOrEqual(_range.Body, high));
        return Expression.Lambda<Func<TRow, bool>>(body, _range.Parameters);
    }

    private sealed class Holder<T>(T value)
    {
        public T Value { get; } = value;
    }
}

internal static class AnalyticsEngine
{
    private static readonly string[] KeyBindingNames = ["S1", "S2", "S3", "N1", "N2", "N3"];

    private static readonly MethodInfo CountMethod = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1);

    private static readonly MethodInfo DistinctMethod = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Distinct) && m.GetParameters().Length == 1);

    private static readonly MethodInfo SelectMethod = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Select) && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

    private static MethodInfo Aggregate(string name, Type slot)
    {
        if (name is nameof(Enumerable.Min) or nameof(Enumerable.Max))
        {
            return typeof(Enumerable).GetMethods()
                .Single(m => m.Name == name && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 2)
                .MakeGenericMethod(slot, typeof(decimal?));
        }

        return typeof(Enumerable).GetMethods()
            .Single(m => m.Name == name && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 2
                         && m.GetParameters()[1].ParameterType.GetGenericArguments()[1] == typeof(decimal?))
            .MakeGenericMethod(slot);
    }

    /// <summary>project -> group -> aggregate -> order -> take, as ONE query.</summary>
    public static IQueryable<AnalyticsCell> Group<TRow>(
        IQueryable<TRow> source, IReadOnlyList<AnalyticsDimension<TRow>> dims, IReadOnlyList<AnalyticsMeasure<TRow>> measures,
        bool sortIsMeasure, int sortIndex, bool descending, int take)
    {
        // 1. flatten each row into the slot shape (unused slots are null constants)
        var row = Expression.Parameter(typeof(TRow), "row");
        var slotBindings = new List<MemberBinding>();
        for (var i = 0; i < AnalyticsLimits.MaxDimensions; i++)
        {
            var dim = i < dims.Count ? dims[i] : null;
            slotBindings.Add(Expression.Bind(typeof(AnalyticsSlot).GetProperty($"S{i + 1}")!, dim is null ? Expression.Constant(null, typeof(string)) : Rebind(dim.Value, row)));
            slotBindings.Add(Expression.Bind(typeof(AnalyticsSlot).GetProperty($"N{i + 1}")!,
                dim?.Order is null ? Expression.Constant(null, typeof(long?)) : Rebind(dim.Order, row)));
        }

        for (var i = 0; i < AnalyticsLimits.MaxMeasures; i++)
        {
            var measure = i < measures.Count ? measures[i] : null;
            slotBindings.Add(Expression.Bind(typeof(AnalyticsSlot).GetProperty($"V{i + 1}")!,
                measure?.Number is null ? Expression.Constant(null, typeof(decimal?)) : Rebind(measure.Number, row)));
            slotBindings.Add(Expression.Bind(typeof(AnalyticsSlot).GetProperty($"K{i + 1}")!,
                measure?.DistinctKey is null ? Expression.Constant(null, typeof(string)) : Rebind(measure.DistinctKey, row)));
        }

        var toSlot = Expression.Lambda<Func<TRow, AnalyticsSlot>>(Expression.MemberInit(Expression.New(typeof(AnalyticsSlot)), slotBindings), row);
        var slots = source.Select(toSlot);

        // 2. group by the dimension texts (and their order numbers)
        var slot = Expression.Parameter(typeof(AnalyticsSlot), "s");
        var keyBindings = KeyBindingNames
            .Select(p => (MemberBinding)Expression.Bind(typeof(AnalyticsGroupKey).GetProperty(p)!, Expression.Property(slot, p))).ToList();
        var keyLambda = Expression.Lambda<Func<AnalyticsSlot, AnalyticsGroupKey>>(Expression.MemberInit(Expression.New(typeof(AnalyticsGroupKey)), keyBindings), slot);

        // 3. aggregate per group
        var group = Expression.Parameter(typeof(IGrouping<AnalyticsGroupKey, AnalyticsSlot>), "g");
        var cellBindings = new List<MemberBinding>();
        foreach (var p in new[] { "S1", "S2", "S3", "N1", "N2", "N3" })
        {
            cellBindings.Add(Expression.Bind(typeof(AnalyticsCell).GetProperty(p)!, Expression.Property(Expression.Property(group, "Key"), p)));
        }

        for (var i = 0; i < AnalyticsLimits.MaxMeasures; i++)
        {
            Expression value = i < measures.Count ? MeasureOf(measures[i].Aggregation, group, i + 1) : Expression.Constant(null, typeof(decimal?));
            cellBindings.Add(Expression.Bind(typeof(AnalyticsCell).GetProperty($"M{i + 1}")!, value));
        }

        var cellLambda = Expression.Lambda<Func<IGrouping<AnalyticsGroupKey, AnalyticsSlot>, AnalyticsCell>>(
            Expression.MemberInit(Expression.New(typeof(AnalyticsCell)), cellBindings), group);
        IQueryable<AnalyticsCell> cells = slots.GroupBy(keyLambda).Select(cellLambda);

        // 4. deterministic order: the chosen column, then every key (so a tie never changes the page), then take
        var orderColumns = new List<(string Property, bool Descending)>();
        if (sortIsMeasure)
        {
            orderColumns.Add(($"M{sortIndex + 1}", descending));
        }
        else
        {
            orderColumns.Add((dims[sortIndex].Order is null ? $"S{sortIndex + 1}" : $"N{sortIndex + 1}", descending));
        }

        foreach (var key in new[] { "N1", "S1", "N2", "S2", "N3", "S3" })
        {
            if (orderColumns.All(c => c.Property != key))
            {
                orderColumns.Add((key, false));
            }
        }

        IOrderedQueryable<AnalyticsCell>? ordered = null;
        foreach (var (property, desc) in orderColumns)
        {
            ordered = OrderBy(ordered ?? cells, property, desc, first: ordered is null);
        }

        return ordered!.Take(take);
    }

    private static Expression MeasureOf(Aggregation aggregation, ParameterExpression group, int position)
    {
        var slotType = typeof(AnalyticsSlot);
        var s = Expression.Parameter(slotType, "x");

        switch (aggregation)
        {
            case Aggregation.Count:
                return Expression.Convert(Expression.Call(CountMethod.MakeGenericMethod(slotType), group), typeof(decimal?));

            case Aggregation.CountDistinct:
            {
                var keys = Expression.Call(SelectMethod.MakeGenericMethod(slotType, typeof(string)), group,
                    Expression.Lambda<Func<AnalyticsSlot, string?>>(Expression.Property(s, $"K{position}"), s));
                var distinct = Expression.Call(DistinctMethod.MakeGenericMethod(typeof(string)), keys);
                return Expression.Convert(Expression.Call(CountMethod.MakeGenericMethod(typeof(string)), distinct), typeof(decimal?));
            }

            default:
            {
                var name = aggregation switch
                {
                    Aggregation.Sum => nameof(Enumerable.Sum),
                    Aggregation.Avg => nameof(Enumerable.Average),
                    Aggregation.Min => nameof(Enumerable.Min),
                    _ => nameof(Enumerable.Max)
                };
                var number = Expression.Lambda<Func<AnalyticsSlot, decimal?>>(Expression.Property(s, $"V{position}"), s);
                return Expression.Call(Aggregate(name, slotType), group, number);
            }
        }
    }

    private static IOrderedQueryable<AnalyticsCell> OrderBy(IQueryable<AnalyticsCell> source, string property, bool descending, bool first)
    {
        var p = Expression.Parameter(typeof(AnalyticsCell), "c");
        var member = Expression.Property(p, property);
        var lambda = Expression.Lambda(member, p);
        var name = (first, descending) switch
        {
            (true, false) => nameof(Queryable.OrderBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (false, false) => nameof(Queryable.ThenBy),
            _ => nameof(Queryable.ThenByDescending)
        };
        var method = typeof(Queryable).GetMethods().Single(m => m.Name == name && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(AnalyticsCell), member.Type);
        return (IOrderedQueryable<AnalyticsCell>)method.Invoke(null, [source, lambda])!;
    }

    /// <summary>Re-binds the parameter of a catalog expression to the shared row parameter.</summary>
    private static Expression Rebind(LambdaExpression lambda, ParameterExpression to) => new ParameterRebinder(lambda.Parameters[0], to).Visit(lambda.Body);

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>The cells -> the result: labels formatted, the extra row dropped (and reported as truncation), the previous period matched by key.</summary>
    public static AnalyticsResult Shape(
        IReadOnlyList<AnalyticsCell> cells, IReadOnlyList<AnalyticsCell>? previous,
        IReadOnlyList<(string Key, string Title, Func<string, string>? Format)> dims, IReadOnlyList<(string Key, string Title)> measures,
        AnalyticsQuery q, DateTimeOffset? previousFrom, DateTimeOffset? previousTo)
    {
        var truncated = cells.Count > q.TopN;
        var page = cells.Take(q.TopN).ToList();

        string?[] Labels(AnalyticsCell c)
        {
            var raw = new[] { c.S1, c.S2, c.S3 };
            return dims.Select((d, i) => raw[i] is { } text ? d.Format?.Invoke(text) ?? text : null).ToArray();
        }

        decimal?[] Values(AnalyticsCell c) => new[] { c.M1, c.M2, c.M3, c.M4 }.Take(measures.Count).ToArray();

        string KeyOf(AnalyticsCell c) => string.Join('\u001F', new[] { c.S1, c.S2, c.S3 }.Take(dims.Count).Select(s => s ?? "\u0000"));

        var before = previous?.GroupBy(KeyOf).ToDictionary(g => g.Key, g => Values(g.First()));
        var rows = page.Select(c => new AnalyticsRow(
            Labels(c), Values(c), before is null ? null : before.TryGetValue(KeyOf(c), out var old) ? old : null)).ToList();

        return new AnalyticsResult(
            dims.Select(d => new AnalyticsColumn(d.Key, d.Title)).ToList(), measures.Select(m => new AnalyticsColumn(m.Key, m.Title)).ToList(),
            rows, truncated, q.From, q.To, previousFrom, previousTo);
    }

}

/// <summary>
/// The labels of the calendar time grains. A grain dimension groups by a number (<c>year*10000 + month*100 + day</c>, <c>year*100 + month</c>,
/// <c>year*10 + quarter</c> or the year) and orders by the same number; the text of the key is that number and these turn it into what a
/// person reads. A key that does not fit its pattern is shown as it is.
/// </summary>
public static class TimeGrain
{
    /// <summary>Formats a day key <c>yyyyMMdd</c> as <c>yyyy/MM/dd</c>.</summary>
    public static string Day(string key) => key.Length == 8 ? $"{key[..4]}/{key.Substring(4, 2)}/{key.Substring(6, 2)}" : key;

    /// <summary>Formats a month key <c>yyyyMM</c> as <c>yyyy/MM</c>.</summary>
    public static string Month(string key) => key.Length == 6 ? $"{key[..4]}/{key.Substring(4, 2)}" : key;

    /// <summary>Formats a quarter key <c>yyyyQ</c> as <c>yyyy Qn</c>.</summary>
    public static string Quarter(string key) => key.Length == 5 ? $"{key[..4]} Q{key[4]}" : key;

    /// <summary>A year key is shown as it is.</summary>
    public static string Year(string key) => key;

    /// <summary>The text of a grain number, culture-independent.</summary>
    public static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
