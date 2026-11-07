using System.Linq.Expressions;

namespace ChronoAudit.Core.Tests;

/// <summary>The typed filter of the analytics core, checked by running the expression in memory (every operator and the depth-3 shape).</summary>
public sealed class AnalyticsFilterTests
{
    public enum Phase { Draft = 1, Done = 2, Archived = 3 }

    public sealed class Row
    {
        public Guid Id { get; set; }

        public string NameNormalized { get; set; } = string.Empty;

        public string? Code { get; set; }

        public int Count { get; set; }

        public decimal? Amount { get; set; }

        public DateTimeOffset At { get; set; }

        public Phase Phase { get; set; }

        public bool Flag { get; set; }
    }

    // Persian names written as escapes: Ali Rezaei, Ali Ahmadi, Sara Karimi, Maryam Hosseini (Persian yeh U+06CC, kaf U+06A9).
    private const string Ali = "علی";
    private const string AliArabic = "علي";

    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly FilterBuilder<Row> Builder = new(
        [
            FilterField.NormalizedText<Row>("name", "Name", r => r.NameNormalized, FilterOp.Eq, FilterOp.Prefix, FilterOp.In),
            FilterField.Of<Row, string?>("code", "Code", r => r.Code, FilterOp.Eq, FilterOp.Prefix, FilterOp.IsNull),
            FilterField.Of<Row, int>("count", "Count", r => r.Count, FilterOp.Eq, FilterOp.In, FilterOp.Range),
            FilterField.Of<Row, decimal?>("amount", "Amount", r => r.Amount, FilterOp.Range, FilterOp.IsNull),
            FilterField.Of<Row, DateTimeOffset>("at", "Time", r => r.At, FilterOp.Range),
            FilterField.Of<Row, Phase>("phase", "Phase", r => r.Phase, FilterOp.Eq, FilterOp.In),
            FilterField.Of<Row, bool>("flag", "Flag", r => r.Flag, FilterOp.Eq),
        ],
        normalizer: PersianTextNormalizer.Instance);

    private static readonly List<Row> Rows =
    [
        new() { Id = Guid.NewGuid(), NameNormalized = Ali + " رضایی", Code = "A-1", Count = 1, Amount = 10m, At = T0, Phase = Phase.Draft, Flag = true },
        new() { Id = Guid.NewGuid(), NameNormalized = Ali + " احمدی", Code = "A-2", Count = 5, Amount = null, At = T0.AddDays(10), Phase = Phase.Done },
        new() { Id = Guid.NewGuid(), NameNormalized = "سارا کریمی", Code = null, Count = 9, Amount = 99.5m, At = T0.AddDays(20), Phase = Phase.Done, Flag = true },
        new() { Id = Guid.NewGuid(), NameNormalized = "مریم حسینی", Code = "B-1", Count = 12, Amount = 500m, At = T0.AddDays(30), Phase = Phase.Archived },
    ];

    private static List<int> Counts(params FilterNode[] nodes)
    {
        var result = Builder.Build(nodes);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);
        return Rows.Where(result.Value.Compile()).Select(r => r.Count).OrderBy(c => c).ToList();
    }

    private static ChronoAuditError Refused(params FilterNode[] nodes)
    {
        var result = Builder.Build(nodes);
        Assert.True(result.IsFailure);
        Assert.Equal("Filter.NotAllowed", result.Error.Code);
        Assert.Equal(ChronoAuditErrorKind.Validation, result.Error.Kind);
        return result.Error;
    }

    [Fact]
    public void Eq_In_Range_Prefix_IsNull_EachSelectTheRightRows()
    {
        Assert.Equal(new[] { 5 }, Counts(FilterNode.Condition("count", FilterOp.Eq, "5")));
        Assert.Equal(new[] { 1, 12 }, Counts(FilterNode.Condition("count", FilterOp.In, "1", "12", "7")));
        Assert.Equal(new[] { 5, 9 }, Counts(FilterNode.Condition("count", FilterOp.Range, "5", "9")));
        Assert.Equal(new[] { 9, 12 }, Counts(FilterNode.Condition("count", FilterOp.Range, "9", string.Empty)));
        Assert.Equal(new[] { 1, 5 }, Counts(FilterNode.Condition("code", FilterOp.Prefix, "A-")));
        Assert.Equal(new[] { 9 }, Counts(FilterNode.Condition("code", FilterOp.IsNull)));
        Assert.Equal(new[] { 5 }, Counts(FilterNode.Condition("amount", FilterOp.IsNull)));
        Assert.Equal(new[] { 9, 12 }, Counts(FilterNode.Condition("amount", FilterOp.Range, "50", string.Empty)));
        Assert.Equal(new[] { 5, 9, 12 }, Counts(FilterNode.Condition("phase", FilterOp.In, "Done", "archived")));
        Assert.Equal(new[] { 1, 9 }, Counts(FilterNode.Condition("flag", FilterOp.Eq, "true")));
        Assert.Equal(new[] { 5, 9 }, Counts(FilterNode.Condition("at", FilterOp.Range, "2026-03-05T00:00:00Z", "2026-03-25T00:00:00Z")));
        Assert.Equal(new[] { 1, 5, 9, 12 }, Counts());
    }

    [Fact]
    public void TheRoot_IsAnAnd_OfItsNodes()
    {
        Assert.Equal(new[] { 9 }, Counts(FilterNode.Condition("phase", FilterOp.Eq, "Done"), FilterNode.Condition("flag", FilterOp.Eq, "true")));
    }

    [Fact]
    public void AnAndOfAnOrOfAnAnd_GivesTheRightRows()
    {
        // phase = Done AND ( count = 1 OR ( flag = true AND amount >= 50 ) )  ->  only the third row (Done, flag, 99.5)
        var rows = Counts(
            FilterNode.Condition("phase", FilterOp.Eq, "Done"),
            FilterNode.AnyOf(
                FilterNode.Condition("count", FilterOp.Eq, "1"),
                FilterNode.AllOf(FilterNode.Condition("flag", FilterOp.Eq, "true"), FilterNode.Condition("amount", FilterOp.Range, "50", string.Empty))));

        Assert.Equal(new[] { 9 }, rows);
    }

    [Theory]
    [InlineData(AliArabic)]
    [InlineData(Ali)]
    public void ArabicAndPersianYeh_FindTheSameRows(string typed)
    {
        Assert.Equal(new[] { 1, 5 }, Counts(FilterNode.Condition("name", FilterOp.Prefix, typed)));
    }

    [Fact]
    public void ArabicKaf_AndPersianDigitsInTheValue_AreNormalizedBeforeTheComparison()
    {
        // "Sara Karimi" typed with Arabic kaf (U+0643) and Arabic yeh (U+064A); the digits 5 and 9 typed as Persian and Arabic-Indic digits.
        Assert.Equal(new[] { 9 }, Counts(FilterNode.Condition("name", FilterOp.Eq, "سارا كريمي")));
        Assert.Equal(new[] { 5, 9 }, Counts(FilterNode.Condition("count", FilterOp.In, "۵", "٩")));
    }

    [Fact]
    public void WithoutANormalizer_TextIsComparedAsTyped()
    {
        var plain = new FilterBuilder<Row>([FilterField.NormalizedText<Row>("name", "Name", r => r.NameNormalized, FilterOp.Prefix)]);

        var filter = plain.Build([FilterNode.Condition("name", FilterOp.Prefix, AliArabic)]).Value.Compile();

        Assert.DoesNotContain(Rows, filter.Invoke);
    }

    [Fact]
    public void AnUnknownKey_IsRefused_NamingIt_AndNeverReadAsAColumn()
    {
        var error = Refused(FilterNode.Condition("Password; DROP TABLE", FilterOp.Eq, "x"));

        Assert.Contains("Password; DROP TABLE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOperatorTheFieldDoesNotAllow_IsRefused()
    {
        Assert.Contains("Flag", Refused(FilterNode.Condition("flag", FilterOp.In, "true")).Message, StringComparison.Ordinal);
        Refused(FilterNode.Condition("count", FilterOp.Prefix, "1"));
        Refused(FilterNode.Condition("name", FilterOp.IsNull));
    }

    [Theory]
    [InlineData("count", FilterOp.Eq, "abc")]
    [InlineData("count", FilterOp.Eq, "")]
    [InlineData("phase", FilterOp.Eq, "7")]
    [InlineData("phase", FilterOp.Eq, "Nope")]
    [InlineData("flag", FilterOp.Eq, "maybe")]
    [InlineData("at", FilterOp.Range, "yesterday")]
    public void AValueOfTheWrongType_IsRefused(string field, FilterOp op, string value)
    {
        var node = op == FilterOp.Range ? FilterNode.Condition(field, op, value, string.Empty) : FilterNode.Condition(field, op, value);

        Refused(node);
    }

    [Fact]
    public void WrongValueCounts_AndAnInvertedRange_AreRefused()
    {
        Refused(FilterNode.Condition("count", FilterOp.Eq, "1", "2"));
        Refused(FilterNode.Condition("count", FilterOp.Range, "1"));
        Refused(FilterNode.Condition("count", FilterOp.Range, string.Empty, string.Empty));
        Refused(FilterNode.Condition("count", FilterOp.Range, "9", "1"));
        Refused(FilterNode.Condition("code", FilterOp.IsNull, "x"));
        Refused(FilterNode.Condition("code", FilterOp.Prefix, " "));
        Refused(FilterNode.Condition("code", FilterOp.Prefix, new string('a', 65)));
    }

    [Fact]
    public void AFourthLevel_AnOrInsideAnOr_AnAndAtTheRoot_AndNestedGroupsInAnAnd_AreRefused()
    {
        var leaf = FilterNode.Condition("count", FilterOp.Eq, "1");

        Refused(FilterNode.AnyOf(FilterNode.AllOf(FilterNode.AnyOf(leaf))));
        Refused(FilterNode.AnyOf(FilterNode.AnyOf(leaf)));
        Refused(FilterNode.AllOf(leaf));
        Refused(FilterNode.AnyOf(FilterNode.AllOf(FilterNode.AllOf(leaf))));
    }

    [Fact]
    public void AmbiguousOrEmptyNodes_AreRefused()
    {
        Refused(new FilterNode());
        Refused(new FilterNode(Field: "count", Op: FilterOp.Eq, Values: ["1"], Or: [FilterNode.Condition("count", FilterOp.Eq, "1")]));
        Refused(new FilterNode(Field: "count"));
        Refused(FilterNode.AnyOf());
        Refused(new FilterNode(Or: [null!]));
    }

    [Fact]
    public void TooManyConditions_TooManyMembers_AndATooLongIn_AreRefused()
    {
        var leaf = FilterNode.Condition("count", FilterOp.Eq, "1");

        Refused(Enumerable.Repeat(leaf, 21).ToArray());
        Refused(FilterNode.AnyOf(Enumerable.Repeat(leaf, 11).ToArray()));
        Refused(FilterNode.AnyOf(FilterNode.AllOf(Enumerable.Repeat(leaf, 11).ToArray())));
        Refused(FilterNode.Condition("count", FilterOp.In, Enumerable.Range(1, 51).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        Assert.Equal(new[] { 1 }, Counts(Enumerable.Repeat(leaf, 20).ToArray()));
    }

    [Fact]
    public void TheConditionsInsideGroups_AreCountedToo()
    {
        var leaf = FilterNode.Condition("count", FilterOp.Eq, "1");
        var group = FilterNode.AnyOf(Enumerable.Repeat(leaf, 10).ToArray());

        Refused(group, group, FilterNode.AnyOf(leaf));
    }

    [Fact]
    public void ACatalogThatPairsAnOperatorWithTheWrongColumnType_FailsAtStartUp()
    {
        Assert.Throws<ArgumentException>(() => FilterField.Of<Row, bool>("x", "x", r => r.Flag, FilterOp.Range));
        Assert.Throws<ArgumentException>(() => FilterField.Of<Row, int>("x", "x", r => r.Count, FilterOp.IsNull));
        Assert.Throws<ArgumentException>(() => FilterField.Of<Row, int>("x", "x", r => r.Count, FilterOp.Prefix));
        Assert.Throws<ArgumentException>(() => FilterField.Of<Row, int>("x", "x", r => r.Count));
    }
}
