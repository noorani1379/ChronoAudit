using System.IO.Compression;
using System.Xml.Linq;

namespace ChronoAudit.Core.Tests;

public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static SheetModel OneRowSheet(string name, params SheetCell[] cells)
    {
        var sheet = new SheetModel(name);
        for (var i = 0; i < cells.Length; i++)
        {
            sheet.Columns.Add(new SheetColumn("C" + (i + 1)));
        }

        sheet.Rows.Add(cells);
        return sheet;
    }

    private static byte[] Build(params SheetModel[] sheets)
    {
        using var stream = new MemoryStream();
        XlsxWriter.Write(stream, sheets);
        return stream.ToArray();
    }

    private static Dictionary<string, string> ReadParts(byte[] file)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    private static XElement Cell(byte[] file, string reference)
    {
        var sheet = XDocument.Parse(ReadParts(file)["xl/worksheets/sheet1.xml"]);
        return sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == reference);
    }

    private static async IAsyncEnumerable<IReadOnlyList<SheetCell>> AsAsync(IEnumerable<IReadOnlyList<SheetCell>> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    [Fact]
    public async Task TheStreamingWriter_WritesTheSameCellsAsTheListWriter()
    {
        var sheet = new SheetModel("Report") { RightToLeft = true };
        sheet.Columns.Add(new SheetColumn("Name", 20));
        sheet.Columns.Add(new SheetColumn("Amount"));
        sheet.Rows.Add([SheetCell.Of("=1+1"), SheetCell.Of(1234.5m)]);
        sheet.Rows.Add([SheetCell.Of("bell\u0007"), SheetCell.Of(7L)]);
        sheet.Rows.Add([SheetCell.Of(new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero)), SheetCell.Of(string.Empty)]);
        using var streamed = new MemoryStream();

        await XlsxWriter.WriteSingleSheetAsync(streamed, "Report", sheet.Columns.ToList(), AsAsync(sheet.Rows), rightToLeft: true, CancellationToken.None);

        var listed = Build(sheet);
        var a = XDocument.Parse(ReadParts(listed)["xl/worksheets/sheet1.xml"]);
        var b = XDocument.Parse(ReadParts(streamed.ToArray())["xl/worksheets/sheet1.xml"]);
        Assert.Equal(ReadParts(listed).Keys.Order(), ReadParts(streamed.ToArray()).Keys.Order());
        Assert.Equal(a.Descendants(Main + "sheetData").Single().ToString(), b.Descendants(Main + "sheetData").Single().ToString());
        Assert.Equal(a.Descendants(Main + "cols").Single().ToString(), b.Descendants(Main + "cols").Single().ToString());
        Assert.Equal("1", (string?)b.Descendants(Main + "sheetView").Single().Attribute("rightToLeft"));
        Assert.Null(b.Descendants(Main + "dimension").SingleOrDefault());
    }

    [Fact]
    public async Task TheStreamingWriter_WithNoRows_IsStillAValidFileWithTheHeaderRow()
    {
        using var streamed = new MemoryStream();

        await XlsxWriter.WriteSingleSheetAsync(streamed, "Empty", [new SheetColumn("Only")], AsAsync([]), rightToLeft: false, CancellationToken.None);

        var sheet = XDocument.Parse(ReadParts(streamed.ToArray())["xl/worksheets/sheet1.xml"]);
        Assert.Single(sheet.Descendants(Main + "row"));
        Assert.Equal("Only", sheet.Descendants(Main + "t").Single().Value);
    }

    [Fact]
    public async Task TheStreamingWriter_StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var streamed = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XlsxWriter.WriteSingleSheetAsync(
            streamed, "x", [new SheetColumn("c")], AsAsync([[SheetCell.Of("a")]]), rightToLeft: false, cts.Token));
    }

    [Fact]
    public void TheZip_HoldsExactlyTheListedParts_AndEveryPartIsWellFormedXml()
    {
        var parts = ReadParts(Build(OneRowSheet("One", SheetCell.Of("a")), OneRowSheet("Two", SheetCell.Of(1m))));

        Assert.Equal(
            new[]
            {
                "[Content_Types].xml", "_rels/.rels", "docProps/app.xml", "docProps/core.xml", "xl/_rels/workbook.xml.rels",
                "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml",
            }.Order(),
            parts.Keys.Order());
        foreach (var (name, content) in parts)
        {
            XDocument.Parse(content);
            Assert.False(content.Contains("utf-16", StringComparison.OrdinalIgnoreCase), name);
        }

        var workbook = XDocument.Parse(parts["xl/workbook.xml"]);
        Assert.Equal(new[] { "One", "Two" }, workbook.Descendants(Main + "sheet").Select(s => (string?)s.Attribute("name")));
    }

    [Fact]
    public void ANumberCell_HasAValue_AndNoStringType_AndTheNumberStyle()
    {
        var file = Build(OneRowSheet("S", SheetCell.Of(1234.5m), SheetCell.Of(7L)));

        var number = Cell(file, "A2");
        Assert.Equal("1234.5", (string?)number.Element(Main + "v"));
        Assert.Null(number.Attribute("t"));
        Assert.Equal("2", (string?)number.Attribute("s"));
        Assert.Equal("3", (string?)Cell(file, "B2").Attribute("s"));
    }

    [Fact]
    public void FormulaLookingText_IsAnInlineString_NeverAFormula()
    {
        var file = Build(OneRowSheet("S", SheetCell.Of("=HYPERLINK(\"http://x\")")));

        var cell = Cell(file, "A2");
        Assert.Equal("inlineStr", (string?)cell.Attribute("t"));
        Assert.Equal("=HYPERLINK(\"http://x\")", (string?)cell.Descendants(Main + "t").Single());
        Assert.Empty(ReadParts(file)["xl/worksheets/sheet1.xml"].Split("<f>").Skip(1));
        Assert.Null(cell.Element(Main + "f"));
    }

    [Fact]
    public void AControlCharacter_IsRemoved_AndEveryPartStillParses()
    {
        var text = "a" + (char)1 + "b" + (char)0xFFFE + "c\tDم‌";

        var file = Build(OneRowSheet("S", SheetCell.Of(text)));

        foreach (var content in ReadParts(file).Values)
        {
            XDocument.Parse(content);
        }

        Assert.Equal("abc\tDم‌", (string?)Cell(file, "A2").Descendants(Main + "t").Single());
    }

    [Fact]
    public void AnEmptySheet_WithColumnsButNoRows_IsValid()
    {
        var sheet = new SheetModel("Empty");
        sheet.Columns.Add(new SheetColumn("Name"));
        sheet.Columns.Add(new SheetColumn("Amount", 10));

        var file = Build(sheet);

        var doc = XDocument.Parse(ReadParts(file)["xl/worksheets/sheet1.xml"]);
        Assert.Equal("A1:B1", (string?)doc.Descendants(Main + "dimension").Single().Attribute("ref"));
        Assert.Equal(2, doc.Descendants(Main + "row").Descendants(Main + "c").Count());
        Assert.Single(doc.Descendants(Main + "row"));
        Assert.Equal("Name", (string?)Cell(file, "A1").Descendants(Main + "t").Single());
        Assert.Equal("1", (string?)Cell(file, "A1").Attribute("s"));
    }

    [Fact]
    public void ASheetWithNothingAtAll_IsStillValid()
    {
        var file = Build(new SheetModel("Nothing"));

        var doc = XDocument.Parse(ReadParts(file)["xl/worksheets/sheet1.xml"]);
        Assert.Equal("A1", (string?)doc.Descendants(Main + "dimension").Single().Attribute("ref"));
        Assert.Empty(doc.Descendants(Main + "cols"));
    }

    [Fact]
    public void RightToLeft_SetsTheAttribute_OnlyWhenAsked()
    {
        var rtl = new SheetModel("R") { RightToLeft = true };
        var ltr = new SheetModel("L");

        var file = Build(rtl, ltr);

        var parts = ReadParts(file);
        Assert.Equal("1", (string?)XDocument.Parse(parts["xl/worksheets/sheet1.xml"]).Descendants(Main + "sheetView").Single().Attribute("rightToLeft"));
        Assert.Null(XDocument.Parse(parts["xl/worksheets/sheet2.xml"]).Descendants(Main + "sheetView").Single().Attribute("rightToLeft"));
    }

    [Fact]
    public void TheHeaderRowIsFrozen_AndAColumnWidthIsWritten()
    {
        var sheet = new SheetModel("S");
        sheet.Columns.Add(new SheetColumn("A", 22.5));

        var doc = XDocument.Parse(ReadParts(Build(sheet))["xl/worksheets/sheet1.xml"]);

        var pane = doc.Descendants(Main + "pane").Single();
        Assert.Equal("frozen", (string?)pane.Attribute("state"));
        Assert.Equal("1", (string?)pane.Attribute("ySplit"));
        Assert.Equal("22.5", (string?)doc.Descendants(Main + "col").Single().Attribute("width"));
    }

    [Fact]
    public void ADateCell_IsAnOaDateNumber_WithTheDateStyle()
    {
        var instant = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(3.5));

        var file = Build(OneRowSheet("S", SheetCell.Of(instant)));

        var cell = Cell(file, "A2");
        Assert.Equal("4", (string?)cell.Attribute("s"));
        var value = double.Parse((string)cell.Element(Main + "v")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(instant.UtcDateTime, DateTime.FromOADate(value), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ACellLongerThanTheExcelLimit_IsCut()
    {
        var file = Build(OneRowSheet("S", SheetCell.Of(new string('x', XlsxWriter.MaxCellLength + 100))));

        Assert.Equal(XlsxWriter.MaxCellLength, ((string)Cell(file, "A2").Descendants(Main + "t").Single()).Length);
    }

    [Fact]
    public void ColumnsBeyondZ_GetTwoLetterReferences()
    {
        var sheet = new SheetModel("Wide");
        sheet.Rows.Add(Enumerable.Range(0, 28).Select(i => SheetCell.Of((long)i)).ToArray());

        var file = Build(sheet);

        Assert.NotNull(Cell(file, "Z2"));
        Assert.NotNull(Cell(file, "AA2"));
        Assert.NotNull(Cell(file, "AB2"));
    }

    [Fact]
    public void TheSameSheets_GiveTheSameBytes()
    {
        Assert.Equal(Build(OneRowSheet("S", SheetCell.Of("a"))), Build(OneRowSheet("S", SheetCell.Of("a"))));
    }

    [Fact]
    public void Write_LeavesTheStreamOpen_AndRefusesNoSheets()
    {
        using var stream = new MemoryStream();

        XlsxWriter.Write(stream, [new SheetModel("S")]);

        Assert.True(stream.CanWrite);
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write(stream, []));
        Assert.Throws<ArgumentNullException>(() => XlsxWriter.Write(null!, [new SheetModel("S")]));
    }

    [Fact]
    public void Write_MakesInvalidOrDuplicateSheetNamesValid()
    {
        var file = Build(new SheetModel("a/b"), new SheetModel("A/B"));

        var names = XDocument.Parse(ReadParts(file)["xl/workbook.xml"]).Descendants(Main + "sheet").Select(s => (string?)s.Attribute("name")).ToArray();
        Assert.Equal(new[] { "ab", "AB (2)" }, names);
    }

    [Fact]
    public void UniqueSheetNames_HandlesClashesInvalidCharactersAndTheLengthLimit()
    {
        var longName = new string('n', 40);

        var names = XlsxWriter.UniqueSheetNames(["Data", "data", "a:b\\c/d?e*f[g]h'i", null, "  ", longName, longName, "History"], "Sheet");

        Assert.Equal("Data", names[0]);
        Assert.Equal("data (2)", names[1]);
        Assert.Equal("abcdefghi", names[2]);
        Assert.Equal("Sheet 4", names[3]);
        Assert.Equal("Sheet 5", names[4]);
        Assert.Equal(new string('n', 31), names[5]);
        Assert.Equal(new string('n', 27) + " (2)", names[6]);
        Assert.Equal("History (1)", names[7]);
        Assert.All(names, n => Assert.True(n.Length <= 31));
        Assert.Equal(names.Count, names.Select(n => n.ToUpperInvariant()).Distinct().Count());
    }
}
