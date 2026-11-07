namespace ChronoAudit.Core.Tests;

/// <summary>The generic analytics engine, run in memory: every dimension, measure, order and cut. The one-statement translation is proven in the EF tests.</summary>
public sealed class AnalyticsEngineTests
{
    public sealed class Sale
    {
        public Guid Id { get; set; }

        public string Group { get; set; } = string.Empty;

        public string Product { get; set; } = string.Empty;

        public string Buyer { get; set; } = string.Empty;

        public decimal? Amount { get; set; }

        public DateTimeOffset At { get; set; }

        public short Year { get; set; }

        public byte Month { get; set; }
    }

    private static readonly DateTimeOffset T0 = new(2026, 3, 21, 0, 0, 0, TimeSpan.Zero);

    private static readonly AnalyticsSubject<Sale> Subject = new(
        "sales",
        "Sales",
        s => s.At,
        [
            new AnalyticsDimension<Sale>("group", "Group", s => s.Group),
            new AnalyticsDimension<Sale>("product", "Product", s => s.Product),
            new AnalyticsDimension<Sale>("month", "Month", s => ((long)s.Year * 100 + s.Month).ToString(System.Globalization.CultureInfo.InvariantCulture), s => (long)s.Year * 100 + s.Month, TimeGrain.Month),
        ],
        [
            AnalyticsMeasure<Sale>.Count("count", "Count"),
            AnalyticsMeasure<Sale>.CountDistinct("buyers", "Distinct buyers", s => s.Buyer),
            AnalyticsMeasure<Sale>.Of("sum", "Sum", Aggregation.Sum, s => s.Amount),
            AnalyticsMeasure<Sale>.Of("avg", "Average", Aggregation.Avg, s => s.Amount),
            AnalyticsMeasure<Sale>.Of("min", "Minimum", Aggregation.Min, s => s.Amount),
            AnalyticsMeasure<Sale>.Of("max", "Maximum", Aggregation.Max, s => s.Amount),
        ],
        [FilterField.Of<Sale, string>("group", "Group", s => s.Group, FilterOp.Eq, FilterOp.In)]);

    private static readonly List<Sale> Rows =
    [
        Row("A", "x", "b1", 10m, 0, month: 1),
        Row("A", "x", "b1", 30m, 1, month: 1),
        Row("A", "y", "b2", 5m, 2, month: 2),
        Row("B", "x", "b3", null, 3, month: 2),
        Row("B", "y", "b3", 100m, 4, month: 2),
        Row("B", "y", "b4", 200m, 5, month: 3),
    ];

    private static Sale Row(string group, string product, string buyer, decimal? amount, int dayOffset, short year = 1405, byte month = 1) => new()
    {
        Id = Guid.NewGuid(), Group = group, Product = product, Buyer = buyer, Amount = amount, At = T0.AddDays(dayOffset), Year = year, Month = month,
    };

    private static AnalyticsQuery Query(
        string[] dims, string[] measures, int topN = 100, string? sortBy = null, bool desc = true, bool compare = false,
        IReadOnlyList<FilterNode>? filters = null, int fromDay = 0, int toDay = 30)
        => new("sales", dims, measures, filters, T0.AddDays(fromDay), T0.AddDays(toDay), topN, sortBy, desc, compare);

    private static AnalyticsResult Run(AnalyticsQuery q, IEnumerable<Sale>? data = null)
    {
        var plan = Subject.Plan(q, (data ?? Rows).AsQueryable());
        Assert.True(plan.IsSuccess, plan.IsFailure ? plan.Error.Message : string.Empty);
        return plan.Value.Shape(plan.Value.Cells.ToList(), plan.Value.PreviousCells?.ToList());
    }

    private static ChronoAuditError Refused(AnalyticsQuery q)
    {
        var plan = Subject.Plan(q, Rows.AsQueryable());
        Assert.True(plan.IsFailure);
        return plan.Error;
    }

    [Fact]
    public void OneDimension_Count_GroupsAndCounts()
    {
        var result = Run(Query(["group"], ["count"], sortBy: "group", desc: false));

        Assert.Equal(new[] { ("A", (decimal?)3m), ("B", (decimal?)3m) }, result.Rows.Select(r => (r.Dimensions[0]!, r.Measures[0])));
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public void ThreeDimensionsAndFourMeasures_AreOneGroupingWithTheRightValues()
    {
        var result = Run(Query(["group", "product", "month"], ["count", "sum", "avg", "buyers"], sortBy: "group", desc: false));

        var ax = result.Rows.Single(r => r.Dimensions[0] == "A" && r.Dimensions[1] == "x");
        Assert.Equal(new decimal?[] { 2m, 40m, 20m, 1m }, ax.Measures);
        var by = result.Rows.Where(r => r.Dimensions[0] == "B" && r.Dimensions[1] == "y").OrderBy(r => r.Dimensions[2]).ToList();
        Assert.Equal(new decimal?[] { 100m, 200m }, by.Select(r => r.Measures[1]));
        Assert.Equal(new[] { "1405/02", "1405/03" }, by.Select(r => r.Dimensions[2]));
    }

    [Fact]
    public void MinMaxSumAvgDistinct_OverAGroupWithNulls_IgnoreTheNulls()
    {
        var result = Run(Query(["group"], ["min", "max", "sum", "avg"], sortBy: "group", desc: false));
        Assert.Equal(new decimal?[] { 100m, 200m, 300m, 150m }, result.Rows.Single(r => r.Dimensions[0] == "B").Measures);

        Assert.Equal(new decimal?[] { 2m, 2m }, Run(Query(["group"], ["buyers"], sortBy: "group", desc: false)).Rows.Select(r => r.Measures[0]));
    }

    [Fact]
    public void ATimeGrain_OrdersChronologically_WhateverTheTextSays()
    {
        var result = Run(Query(["month"], ["count"], sortBy: "month", desc: false));

        Assert.Equal(new[] { "1405/01", "1405/02", "1405/03" }, result.Rows.Select(r => r.Dimensions[0]));
    }

    [Fact]
    public void SortedByAMeasureDescending_TopNKeepsTheBest_AndSaysItWasCut()
    {
        var result = Run(Query(["product", "group"], ["sum"], topN: 2, sortBy: "sum"));

        Assert.Equal(new decimal?[] { 300m, 40m }, result.Rows.Select(r => r.Measures[0]));
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void WithoutSortBy_TheFirstMeasureDescendingIsTheDefault()
    {
        Assert.Equal("B", Run(Query(["group"], ["sum", "count"])).Rows[0].Dimensions[0]);
    }

    [Fact]
    public void ARangeSelectsRows_AndAFilterNarrowsThem()
    {
        Assert.Equal(2m, Run(Query(["group"], ["count"], fromDay: 3, toDay: 4)).Rows.Sum(r => r.Measures[0]));
        var filtered = Run(Query(["group"], ["count"], filters: [FilterNode.Condition("group", FilterOp.Eq, "A")]));
        Assert.Equal("A", Assert.Single(filtered.Rows).Dimensions[0]);
    }

    [Fact]
    public void ComparePrevious_MatchesTheSameGroupInThePreviousPeriodOfEqualLength()
    {
        // current: days 3..5 (group B), previous period of the same length ends the tick before day 3.
        var alone = Run(Query(["group"], ["count"], compare: true, fromDay: 3, toDay: 5));
        Assert.Equal(T0.AddDays(3).AddTicks(-1), alone.PreviousTo);
        Assert.Equal(T0.AddDays(3).AddTicks(-1) - TimeSpan.FromDays(2), alone.PreviousFrom);
        Assert.Null(Assert.Single(alone.Rows).Previous);

        var both = Run(Query(["group"], ["count"], compare: true, fromDay: 3, toDay: 5), [.. Rows, Row("B", "x", "b9", 1m, 1)]);
        Assert.Equal(new decimal?[] { 1m }, both.Rows.Single().Previous);
    }

    [Fact]
    public void AnUnknownDimensionMeasureOrSortKey_IsRefusedAsAFilterNotAllowed_NamingIt()
    {
        foreach (var q in new[]
                 {
                     Query(["Password"], ["count"]), Query(["group"], ["Drop"]), Query(["group"], ["count"], sortBy: "nope"),
                     Query(["group"], ["count"], sortBy: "product"),
                 })
        {
            Assert.Equal("Filter.NotAllowed", Refused(q).Code);
        }

        Assert.Contains("Password", Refused(Query(["Password"], ["count"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyOrDuplicateColumns_ABadTopN_AndABadRange_AreRefused()
    {
        Assert.Equal("Filter.NotAllowed", Refused(Query([], ["count"])).Code);
        Refused(Query(["group", "product", "month", "group"], ["count"]));
        Refused(Query(["group"], []));
        Refused(Query(["group"], ["count", "sum", "avg", "min", "max"]));
        Refused(Query(["group"], ["count", "count"]));
        Refused(Query(["group"], ["count"], topN: 501));
        Refused(Query(["group"], ["count"], topN: 0));
        Refused(Query(["group"], ["count"], fromDay: 10, toDay: 5));
        Assert.Equal("Analytics.RangeTooWide", Refused(Query(["group"], ["count"], fromDay: 0, toDay: 1200)).Code);
    }

    [Fact]
    public void AFilterTheCatalogRefuses_IsReturnedAsIs()
    {
        var error = Refused(Query(["group"], ["count"], filters: [FilterNode.Condition("secret", FilterOp.Eq, "x")]));

        Assert.Equal("Filter.NotAllowed", error.Code);
        Assert.Equal(ChronoAuditErrorKind.Validation, error.Kind);
    }

    [Fact]
    public void ASubjectWhoseMeasureLacksItsExpression_FailsAtStartUp()
    {
        Assert.Throws<ArgumentException>(() => new AnalyticsSubject<Sale>(
            "x", "x", s => s.At, [new AnalyticsDimension<Sale>("g", "g", s => s.Group)], [new AnalyticsMeasure<Sale>("m", "m", Aggregation.Sum)], []));
    }

    [Fact]
    public void TheCatalog_ListsDimensionsMeasuresAndFilterOperators()
    {
        var entry = Subject.Describe();

        Assert.Equal("sales", entry.Key);
        Assert.Equal(new[] { "group", "product", "month" }, entry.Dimensions.Select(d => d.Key));
        Assert.Subset(entry.Measures.Select(m => m.Key).ToHashSet(), new HashSet<string> { "count", "buyers", "sum" });
        Assert.Equal(new[] { FilterOp.Eq, FilterOp.In }, Assert.Single(entry.Filters).Operators);
    }

    [Fact]
    public void TheSubject_ExposesTheNonGenericFace_AndTheAllScopeFlag()
    {
        var restricted = new AnalyticsSubject<Sale>(
            "r", "R", s => s.At, [new AnalyticsDimension<Sale>("g", "g", s => s.Group)], [AnalyticsMeasure<Sale>.Count("count", "Count")], [])
        {
            RequiresAllScope = true,
        };
        IReadOnlyList<IAnalyticsSubject> subjects = [Subject, restricted];

        Assert.Equal(new[] { "sales", "r" }, subjects.Select(x => x.Key));
        Assert.Equal(new[] { false, true }, subjects.Select(x => x.RequiresAllScope));
        Assert.Equal(new[] { "sales", "r" }, subjects.Select(x => x.Describe().Key));
    }

    [Fact]
    public void TheTimeGrains_FormatTheirKeys()
    {
        Assert.Equal("2026/11/01", TimeGrain.Day("20261101"));
        Assert.Equal("2026/11", TimeGrain.Month("202611"));
        Assert.Equal("2026 Q4", TimeGrain.Quarter("20264"));
        Assert.Equal("2026", TimeGrain.Year("2026"));
    }
}
