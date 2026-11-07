using System.Text;

namespace ChronoAudit.Core.Tests;

public class CsvWriterTests
{
    private sealed record Row(string? Name, decimal Amount, DateTimeOffset At);

    private static readonly CsvColumn<Row>[] Columns =
    [
        new("Name", r => r.Name),
        new("Amount", r => CsvWriter.Number(r.Amount), IsText: false),
        new("At", r => CsvWriter.Instant(r.At), IsText: false),
    ];

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("line1\r\nline2", "\"line1\r\nline2\"")]
    [InlineData("", "")]
    public void Escape_QuotesOnlyWhenNeeded(string input, string expected)
    {
        Assert.Equal(expected, CsvWriter.Escape(input, isText: true));
    }

    [Fact]
    public void Escape_NullIsAnEmptyCell()
    {
        Assert.Equal(string.Empty, CsvWriter.Escape(null, isText: true));
        Assert.Equal(string.Empty, CsvWriter.Escape(null, isText: false));
    }

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tx", "'\tx")]
    public void FormulaText_IsNeutralized(string input, string expected)
    {
        Assert.Equal(expected, CsvWriter.Escape(input, isText: true));
    }

    [Fact]
    public void ANeutralizedValueWithAComma_IsStillQuoted()
    {
        Assert.Equal("\"'=A1,B1\"", CsvWriter.Escape("=A1,B1", isText: true));
    }

    [Fact]
    public void ACarriageReturnPrefix_IsNeutralizedAndThenQuoted()
    {
        Assert.Equal("\"'\rx\"", CsvWriter.Escape("\rx", isText: true));
    }

    [Fact]
    public void NonTextCells_AreNotNeutralized_SoNegativeNumbersStayNumbers()
    {
        Assert.Equal("-5", CsvWriter.Escape("-5", isText: false));
        Assert.Equal("-5", CsvWriter.Escape(CsvWriter.Number(-5m), isText: false));
    }

    [Fact]
    public void NonAsciiTextAndZwnj_AreKeptByteForByte()
    {
        var text = "می‌شود";

        var line = CsvWriter.Line(new Row(text, 1m, DateTimeOffset.UnixEpoch), Columns);

        Assert.StartsWith(text + ",", line, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(line)[..Encoding.UTF8.GetByteCount(text)]);
    }

    [Fact]
    public void Number_UsesTheInvariantCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            Assert.Equal("1234.5", CsvWriter.Number(1234.5m));
            Assert.Equal("-0.25", CsvWriter.Number(-0.25m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Instant_IsUtcWithSevenFractionDigits()
    {
        var instant = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(3.5));

        Assert.Equal("2026-11-01T08:30:00.0000000Z", CsvWriter.Instant(instant));
    }

    [Fact]
    public void HeaderLine_AndLine_EndWithCrLf_InColumnOrder()
    {
        var row = new Row("a,b", 2.5m, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        Assert.Equal("Name,Amount,At\r\n", CsvWriter.HeaderLine(Columns));
        Assert.Equal("\"a,b\",2.5,2026-01-02T03:04:05.0000000Z\r\n", CsvWriter.Line(row, Columns));
    }

    [Fact]
    public void ANullCell_IsEmpty_AndHeadersAreNotNeutralized()
    {
        CsvColumn<Row>[] columns = [new("-Delta", r => r.Name), new("B", r => "x")];

        Assert.Equal("-Delta,B\r\n", CsvWriter.HeaderLine(columns));
        Assert.Equal(",x\r\n", CsvWriter.Line(new Row(null, 0m, DateTimeOffset.UnixEpoch), columns));
    }

    [Fact]
    public void Bom_IsTheUtf8ByteOrderMark()
    {
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, CsvWriter.Bom.ToArray());
    }
}
