using System.Globalization;

namespace ChronoAudit;

/// <summary>The Gregorian calendar in a configured time zone.</summary>
public sealed class GregorianChronoAuditCalendar : IChronoAuditCalendar
{
    private readonly TimeZoneInfo _timeZone;

    /// <summary>Creates the calendar.</summary>
    /// <param name="timeZone">The time zone in which the day boundaries are taken.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeZone"/> is null.</exception>
    public GregorianChronoAuditCalendar(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        _timeZone = timeZone;
    }

    /// <inheritdoc />
    public string Name => "Gregorian";

    /// <inheritdoc />
    public CalendarParts PartsOf(DateTimeOffset instantUtc)
    {
        var local = TimeZoneInfo.ConvertTime(instantUtc, _timeZone);
        return new CalendarParts(local.Year, local.Month, local.Day, (local.Month - 1) / 3 + 1);
    }

    /// <inheritdoc />
    public string FormatDay(int year, int month, int day) =>
        string.Create(CultureInfo.InvariantCulture, $"{year:D4}/{month:D2}/{day:D2}");
}
