using System.Globalization;
using System.Text;

namespace ChronoAudit;

/// <summary>One column of a CSV export: its header and how it reads a row.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Header">The header text.</param>
/// <param name="Value">Reads the cell text from a row; null is an empty cell.</param>
/// <param name="IsText">True for free text (a leading formula character is neutralized); false for numbers and instants.</param>
public sealed record CsvColumn<T>(string Header, Func<T, string?> Value, bool IsText = true);

/// <summary>
/// Pure CSV formatting (RFC 4180 quoting, CR LF line ends, UTF-8). Text that a spreadsheet would run as a formula gets a leading
/// apostrophe, so an export cannot be used to plant a formula in the reader's spreadsheet.
/// </summary>
public static class CsvWriter
{
    private static readonly byte[] BomBytes = [0xEF, 0xBB, 0xBF];

    /// <summary>The UTF-8 byte order mark (EF BB BF) that makes spreadsheets read the file as UTF-8.</summary>
    public static ReadOnlySpan<byte> Bom => BomBytes;

    /// <summary>Quotes a cell when it contains a comma, a double quote or a line break; neutralizes formulas in text cells.</summary>
    /// <param name="value">The cell text; null is an empty cell.</param>
    /// <param name="isText">True for free text; the characters <c>=</c> <c>+</c> <c>-</c> <c>@</c>, TAB and CR at the start then get a leading apostrophe.</param>
    /// <returns>The cell as it is written to the file.</returns>
    public static string Escape(string? value, bool isText)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (isText && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>An instant as a UTC ISO 8601 text with seven fraction digits and a trailing <c>Z</c>.</summary>
    /// <param name="value">The instant.</param>
    /// <returns>The text, for example <c>2026-11-01T08:30:00.0000000Z</c>.</returns>
    public static string Instant(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A number in the invariant culture (a dot as the decimal separator, no grouping).</summary>
    /// <param name="value">The number.</param>
    /// <returns>The text.</returns>
    public static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The header line, ending with CR LF. Headers are never formula-neutralized.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="columns">The columns.</param>
    /// <returns>The line.</returns>
    public static string HeaderLine<T>(IReadOnlyList<CsvColumn<T>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return string.Join(',', columns.Select(c => Escape(c.Header, isText: false))) + "\r\n";
    }

    /// <summary>One data line, ending with CR LF.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="row">The row.</param>
    /// <param name="columns">The columns.</param>
    /// <returns>The line.</returns>
    public static string Line<T>(T row, IReadOnlyList<CsvColumn<T>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var line = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            line.Append(Escape(columns[i].Value(row), columns[i].IsText));
            line.Append(i == columns.Count - 1 ? "\r\n" : ",");
        }

        return line.ToString();
    }
}
