namespace ChronoAudit;

/// <summary>The kind of a spreadsheet cell; it decides the cell format.</summary>
public enum SheetCellKind
{
    /// <summary>Text, always stored as an inline string (never a formula).</summary>
    Text = 1,

    /// <summary>A decimal number with a thousands separator.</summary>
    Number = 2,

    /// <summary>A whole number with a thousands separator.</summary>
    Integer = 3,

    /// <summary>An instant, stored as a spreadsheet date number with a date and time format.</summary>
    Date = 4,

    /// <summary>A header text (bold, light grey fill).</summary>
    Header = 5,
}

/// <summary>One column of a sheet: the header text and the width in characters.</summary>
/// <param name="Header">The header text, written in the first row.</param>
/// <param name="Width">The column width in characters.</param>
public sealed record SheetColumn(string Header, double Width = 16);

/// <summary>One cell value.</summary>
/// <param name="Kind">The cell kind.</param>
/// <param name="Text">The text of a text or header cell.</param>
/// <param name="Number">The numeric value of a number, integer or date cell.</param>
public readonly record struct SheetCell(SheetCellKind Kind, string? Text, decimal? Number)
{
    /// <summary>A text cell; null is an empty cell.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The cell.</returns>
    public static SheetCell Of(string? text) => new(SheetCellKind.Text, text, null);

    /// <summary>A decimal number cell.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The cell.</returns>
    public static SheetCell Of(decimal number) => new(SheetCellKind.Number, null, number);

    /// <summary>A whole number cell.</summary>
    /// <param name="integer">The number.</param>
    /// <returns>The cell.</returns>
    public static SheetCell Of(long integer) => new(SheetCellKind.Integer, null, integer);

    /// <summary>A date cell: the instant in UTC, stored as an OADate number with the date style.</summary>
    /// <param name="instantUtc">The instant.</param>
    /// <returns>The cell.</returns>
    public static SheetCell Of(DateTimeOffset instantUtc) => new(SheetCellKind.Date, null, (decimal)instantUtc.UtcDateTime.ToOADate());
}

/// <summary>One worksheet: a name, the columns (header row) and the data rows.</summary>
/// <param name="name">The wanted sheet name; the writer makes it valid and unique.</param>
public sealed class SheetModel(string name)
{
    /// <summary>The wanted sheet name.</summary>
    public string Name { get; } = name;

    /// <summary>The columns; their headers form the first (frozen) row.</summary>
    public IList<SheetColumn> Columns { get; } = new List<SheetColumn>();

    /// <summary>The data rows.</summary>
    public IList<IReadOnlyList<SheetCell>> Rows { get; } = new List<IReadOnlyList<SheetCell>>();

    /// <summary>True for a right-to-left sheet (Persian, Arabic, Hebrew).</summary>
    public bool RightToLeft { get; set; }
}
