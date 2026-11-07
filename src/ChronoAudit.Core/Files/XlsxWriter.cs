using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ChronoAudit;

/// <summary>
/// Turns <see cref="SheetModel"/>s into an .xlsx file without any package: the parts are written with <see cref="ZipArchive"/> and
/// <see cref="XmlWriter"/>. Text goes in as inline strings, so a value that starts with <c>=</c> is text and never a formula.
/// Pure: the same sheets give the same bytes.
/// </summary>
public static class XlsxWriter
{
    /// <summary>The most characters one Excel cell holds; a longer value makes Excel "repair" the file.</summary>
    public const int MaxCellLength = 32767;

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // Style indexes of xl/styles.xml.
    private const int StyleDefault = 0;
    private const int StyleHeader = 1;
    private const int StyleNumber = 2;
    private const int StyleInteger = 3;
    private const int StyleDate = 4;

    // A fixed time stamp keeps the bytes of the same sheets identical.
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Writes the workbook to <paramref name="output"/> and leaves the stream open.</summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="sheets">The sheets, at least one.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sheets"/> is empty.</exception>
    public static void Write(Stream output, IReadOnlyList<SheetModel> sheets)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
        {
            throw new ArgumentException("A workbook needs at least one sheet.", nameof(sheets));
        }

        var names = UniqueSheetNames(sheets.Select(s => s.Name).ToList(), "Sheet");

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", xml => ContentTypes(xml, sheets.Count));
        Part(zip, "_rels/.rels", RootRelationships);
        Part(zip, "docProps/app.xml", AppProperties);
        Part(zip, "docProps/core.xml", CoreProperties);
        Part(zip, "xl/workbook.xml", xml => Workbook(xml, names));
        Part(zip, "xl/_rels/workbook.xml.rels", xml => WorkbookRelationships(xml, sheets.Count));
        Part(zip, "xl/styles.xml", Styles);
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            Part(zip, $"xl/worksheets/sheet{i + 1}.xml", xml => Worksheet(xml, sheet, i == 0));
        }
    }


    /// <summary>
    /// Writes a workbook with one sheet whose rows come from an async sequence, so a sheet of a million rows never sits in memory: each
    /// row goes straight into the zip entry. The parts and styles are the same as <see cref="Write"/>; the only difference is that the
    /// optional <c>dimension</c> element is left out, because the row count is not known up front. The stream is left open.
    /// </summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="sheetName">The wanted sheet name (cleaned like <see cref="UniqueSheetNames"/>).</param>
    /// <param name="columns">The columns; they give the header row.</param>
    /// <param name="rows">The data rows.</param>
    /// <param name="rightToLeft">True for a right-to-left sheet.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the workbook is written.</returns>
    public static async Task WriteSingleSheetAsync(
        Stream output, string sheetName, IReadOnlyList<SheetColumn> columns, IAsyncEnumerable<IReadOnlyList<SheetCell>> rows, bool rightToLeft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var names = UniqueSheetNames([sheetName], "Sheet");

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Part(zip, "[Content_Types].xml", xml => ContentTypes(xml, 1));
        Part(zip, "_rels/.rels", RootRelationships);
        Part(zip, "docProps/app.xml", AppProperties);
        Part(zip, "docProps/core.xml", CoreProperties);
        Part(zip, "xl/workbook.xml", xml => Workbook(xml, names));
        Part(zip, "xl/_rels/workbook.xml.rels", xml => WorkbookRelationships(xml, 1));
        Part(zip, "xl/styles.xml", Styles);

        var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using var stream = entry.Open();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false, CloseOutput = false };
        using var xml = XmlWriter.Create(stream, settings);
        xml.WriteStartDocument(true);
        SheetStart(xml, columns.ToList(), rightToLeft, selected: true, dimension: null);
        var index = 0;

        // The zip entry writes synchronously; awaiting the source between rows keeps the memory flat.
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteRow(xml, index++, row);
        }

        SheetEnd(xml);
        xml.WriteEndDocument();
    }
    /// <summary>
    /// Sheet names from wanted names: at most 31 characters, none of <c>: \ / ? * [ ] '</c>; a clash (any case) or an empty name gets a number.
    /// </summary>
    /// <param name="wanted">The wanted names.</param>
    /// <param name="fallbackPrefix">The prefix of a generated name for an empty one.</param>
    /// <returns>The valid, unique names in the same order.</returns>
    public static IReadOnlyList<string> UniqueSheetNames(IReadOnlyList<string?> wanted, string fallbackPrefix)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var name = new string(Clean(wanted[i] ?? string.Empty, 100).Where(ch => !":\\/?*[]'".Contains(ch, StringComparison.Ordinal)).ToArray()).Trim();
            name = name.Length == 0 ? $"{fallbackPrefix} {i + 1}" : name[..Math.Min(name.Length, 31)];
            if (name.Equals("History", StringComparison.OrdinalIgnoreCase))
            {
                name = $"{name} (1)"; // Excel keeps the name "History" for its own use
            }

            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++)
            {
                var suffix = $" ({n})";
                candidate = name[..Math.Min(name.Length, 31 - suffix.Length)] + suffix;
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// XML 1.0 cannot carry control characters, the two non-characters U+FFFE / U+FFFF or an unpaired surrogate (half of an emoji from
    /// corrupt data), and Excel refuses a cell over <paramref name="maxLength"/> characters: drop the first group and cut the text at
    /// the limit (never in the middle of a surrogate pair) rather than produce a file Excel cannot open.
    /// </summary>
    internal static string Clean(string text, int maxLength = MaxCellLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        for (var i = 0; i < text.Length && builder.Length < maxLength; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                if (builder.Length + 2 > maxLength)
                {
                    break;
                }

                builder.Append(ch).Append(text[++i]);
            }
            else if (!char.IsSurrogate(ch) && (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != (char)0xFFFE && ch != (char)0xFFFF)))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static void Part(ZipArchive zip, string path, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using var stream = entry.Open();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false, CloseOutput = false };
        using var xml = XmlWriter.Create(stream, settings);
        xml.WriteStartDocument(true);
        write(xml);
        xml.WriteEndDocument();
    }

    private static void ContentTypes(XmlWriter xml, int sheetCount)
    {
        xml.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
        Default(xml, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default(xml, "xml", "application/xml");
        Override(xml, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        Override(xml, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        Override(xml, "/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
        Override(xml, "/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");
        for (var i = 1; i <= sheetCount; i++)
        {
            Override(xml, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }

        xml.WriteEndElement();
    }

    private static void Default(XmlWriter xml, string extension, string contentType)
    {
        xml.WriteStartElement("Default");
        xml.WriteAttributeString("Extension", extension);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void Override(XmlWriter xml, string partName, string contentType)
    {
        xml.WriteStartElement("Override");
        xml.WriteAttributeString("PartName", partName);
        xml.WriteAttributeString("ContentType", contentType);
        xml.WriteEndElement();
    }

    private static void RootRelationships(XmlWriter xml)
    {
        xml.WriteStartElement("Relationships", RelNs);
        Relationship(xml, "rId1", OfficeRelNs + "/officeDocument", "xl/workbook.xml");
        Relationship(xml, "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        Relationship(xml, "rId3", OfficeRelNs + "/extended-properties", "docProps/app.xml");
        xml.WriteEndElement();
    }

    private static void Relationship(XmlWriter xml, string id, string type, string target)
    {
        xml.WriteStartElement("Relationship");
        xml.WriteAttributeString("Id", id);
        xml.WriteAttributeString("Type", type);
        xml.WriteAttributeString("Target", target);
        xml.WriteEndElement();
    }

    private static void AppProperties(XmlWriter xml)
    {
        xml.WriteStartElement("Properties", "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties");
        xml.WriteElementString("Application", ChronoAuditInfo.Name);
        xml.WriteEndElement();
    }

    private static void CoreProperties(XmlWriter xml)
    {
        xml.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
        xml.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
        xml.WriteElementString("dc", "creator", "http://purl.org/dc/elements/1.1/", ChronoAuditInfo.Name);
        xml.WriteEndElement();
    }

    private static void Workbook(XmlWriter xml, IReadOnlyList<string> names)
    {
        xml.WriteStartElement("workbook", MainNs);
        xml.WriteAttributeString("xmlns", "r", null, OfficeRelNs);
        xml.WriteStartElement("bookViews");
        xml.WriteStartElement("workbookView");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("sheets");
        for (var i = 0; i < names.Count; i++)
        {
            xml.WriteStartElement("sheet");
            xml.WriteAttributeString("name", names[i]);
            xml.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("r", "id", OfficeRelNs, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WorkbookRelationships(XmlWriter xml, int sheetCount)
    {
        xml.WriteStartElement("Relationships", RelNs);
        for (var i = 1; i <= sheetCount; i++)
        {
            Relationship(xml, $"rId{i}", OfficeRelNs + "/worksheet", $"worksheets/sheet{i}.xml");
        }

        Relationship(xml, $"rId{sheetCount + 1}", OfficeRelNs + "/styles", "styles.xml");
        xml.WriteEndElement();
    }

    private static void Styles(XmlWriter xml)
    {
        xml.WriteStartElement("styleSheet", MainNs);

        xml.WriteStartElement("numFmts");
        xml.WriteAttributeString("count", "2");
        NumFmt(xml, 164, "#,##0.##");
        NumFmt(xml, 165, "yyyy-mm-dd hh:mm");
        xml.WriteEndElement();

        xml.WriteStartElement("fonts");
        xml.WriteAttributeString("count", "2");
        xml.WriteStartElement("font");
        xml.WriteStartElement("sz");
        xml.WriteAttributeString("val", "11");
        xml.WriteEndElement();
        xml.WriteStartElement("name");
        xml.WriteAttributeString("val", "Calibri");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("font");
        xml.WriteElementString("b", null);
        xml.WriteStartElement("sz");
        xml.WriteAttributeString("val", "11");
        xml.WriteEndElement();
        xml.WriteStartElement("name");
        xml.WriteAttributeString("val", "Calibri");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("fills");
        xml.WriteAttributeString("count", "3");
        PatternFill(xml, "none", null);
        PatternFill(xml, "gray125", null);
        PatternFill(xml, "solid", "FFEDEDED");
        xml.WriteEndElement();

        xml.WriteStartElement("borders");
        xml.WriteAttributeString("count", "1");
        xml.WriteStartElement("border");
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            xml.WriteElementString(side, null);
        }

        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("cellStyleXfs");
        xml.WriteAttributeString("count", "1");
        Xf(xml, 0, 0, 0, includeXfId: false);
        xml.WriteEndElement();

        xml.WriteStartElement("cellXfs");
        xml.WriteAttributeString("count", "5");
        Xf(xml, 0, 0, 0, includeXfId: true); // StyleDefault
        Xf(xml, 0, 1, 2, includeXfId: true); // StyleHeader
        Xf(xml, 164, 0, 0, includeXfId: true); // StyleNumber
        Xf(xml, 3, 0, 0, includeXfId: true); // StyleInteger
        Xf(xml, 165, 0, 0, includeXfId: true); // StyleDate
        xml.WriteEndElement();

        xml.WriteStartElement("cellStyles");
        xml.WriteAttributeString("count", "1");
        xml.WriteStartElement("cellStyle");
        xml.WriteAttributeString("name", "Normal");
        xml.WriteAttributeString("xfId", "0");
        xml.WriteAttributeString("builtinId", "0");
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteEndElement();
    }

    private static void NumFmt(XmlWriter xml, int id, string code)
    {
        xml.WriteStartElement("numFmt");
        xml.WriteAttributeString("numFmtId", id.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("formatCode", code);
        xml.WriteEndElement();
    }

    private static void PatternFill(XmlWriter xml, string pattern, string? argb)
    {
        xml.WriteStartElement("fill");
        xml.WriteStartElement("patternFill");
        xml.WriteAttributeString("patternType", pattern);
        if (argb is not null)
        {
            xml.WriteStartElement("fgColor");
            xml.WriteAttributeString("rgb", argb);
            xml.WriteEndElement();
            xml.WriteStartElement("bgColor");
            xml.WriteAttributeString("indexed", "64");
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void Xf(XmlWriter xml, int numFmtId, int fontId, int fillId, bool includeXfId)
    {
        xml.WriteStartElement("xf");
        xml.WriteAttributeString("numFmtId", numFmtId.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("fontId", fontId.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("fillId", fillId.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("borderId", "0");
        if (includeXfId)
        {
            xml.WriteAttributeString("xfId", "0");
        }

        if (numFmtId != 0)
        {
            xml.WriteAttributeString("applyNumberFormat", "1");
        }

        if (fontId != 0)
        {
            xml.WriteAttributeString("applyFont", "1");
        }

        if (fillId != 0)
        {
            xml.WriteAttributeString("applyFill", "1");
        }

        xml.WriteEndElement();
    }

    private static void Worksheet(XmlWriter xml, SheetModel sheet, bool selected)
    {
        var columnCount = Math.Max(sheet.Columns.Count, sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count));
        var lastRow = sheet.Rows.Count + 1;
        SheetStart(xml, sheet.Columns, sheet.RightToLeft, selected, columnCount == 0 ? "A1" : $"A1:{ColumnLetter(columnCount - 1)}{lastRow.ToString(CultureInfo.InvariantCulture)}");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            WriteRow(xml, r, sheet.Rows[r]);
        }

        SheetEnd(xml);
    }

    // Everything up to and including the header row; <sheetData> stays open. A streaming sheet leaves the optional dimension out (the row count is not known yet).
    private static void SheetStart(XmlWriter xml, IList<SheetColumn> columns, bool rightToLeft, bool selected, string? dimension)
    {
        xml.WriteStartElement("worksheet", MainNs);

        if (dimension is not null)
        {
            xml.WriteStartElement("dimension");
            xml.WriteAttributeString("ref", dimension);
            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetViews");
        xml.WriteStartElement("sheetView");
        if (rightToLeft)
        {
            xml.WriteAttributeString("rightToLeft", "1");
        }

        if (selected)
        {
            xml.WriteAttributeString("tabSelected", "1");
        }

        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane");
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("activePane", "bottomLeft");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement();
        xml.WriteStartElement("selection");
        xml.WriteAttributeString("pane", "bottomLeft");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        xml.WriteStartElement("sheetFormatPr");
        xml.WriteAttributeString("defaultRowHeight", "15");
        xml.WriteEndElement();

        if (columns.Count > 0)
        {
            xml.WriteStartElement("cols");
            for (var i = 0; i < columns.Count; i++)
            {
                var index = (i + 1).ToString(CultureInfo.InvariantCulture);
                xml.WriteStartElement("col");
                xml.WriteAttributeString("min", index);
                xml.WriteAttributeString("max", index);
                xml.WriteAttributeString("width", columns[i].Width.ToString("0.0###", CultureInfo.InvariantCulture));
                xml.WriteAttributeString("customWidth", "1");
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData");
        if (columns.Count > 0)
        {
            xml.WriteStartElement("row");
            xml.WriteAttributeString("r", "1");
            for (var c = 0; c < columns.Count; c++)
            {
                WriteCell(xml, ColumnLetter(c) + "1", new SheetCell(SheetCellKind.Header, columns[c].Header, null));
            }

            xml.WriteEndElement();
        }
    }

    private static void WriteRow(XmlWriter xml, int rowIndex, IReadOnlyList<SheetCell> cells)
    {
        var rowNumber = (rowIndex + 2).ToString(CultureInfo.InvariantCulture);
        xml.WriteStartElement("row");
        xml.WriteAttributeString("r", rowNumber);
        for (var c = 0; c < cells.Count; c++)
        {
            WriteCell(xml, ColumnLetter(c) + rowNumber, cells[c]);
        }

        xml.WriteEndElement();
    }

    private static void SheetEnd(XmlWriter xml)
    {
        xml.WriteEndElement();

        xml.WriteStartElement("pageMargins");
        foreach (var (name, value) in new[] { ("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3") })
        {
            xml.WriteAttributeString(name, value);
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteCell(XmlWriter xml, string reference, SheetCell cell)
    {
        var style = cell.Kind switch
        {
            SheetCellKind.Header => StyleHeader,
            SheetCellKind.Number => StyleNumber,
            SheetCellKind.Integer => StyleInteger,
            SheetCellKind.Date => StyleDate,
            _ => StyleDefault,
        };

        xml.WriteStartElement("c");
        xml.WriteAttributeString("r", reference);
        xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        if (cell.Kind is SheetCellKind.Number or SheetCellKind.Integer or SheetCellKind.Date)
        {
            if (cell.Number is { } number)
            {
                xml.WriteElementString("v", number.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (!string.IsNullOrEmpty(cell.Text))
        {
            xml.WriteAttributeString("t", "inlineStr");
            xml.WriteStartElement("is");
            xml.WriteStartElement("t");
            xml.WriteAttributeString("xml", "space", null, "preserve");
            xml.WriteString(Clean(cell.Text));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }

    private static string ColumnLetter(int index)
    {
        var letters = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters.Insert(0, (char)('A' + ((n - 1) % 26)));
        }

        return letters.ToString();
    }
}
