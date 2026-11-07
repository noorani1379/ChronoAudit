using System.Globalization;

namespace ChronoAudit;

/// <summary>The Persian calendar (<see cref="PersianCalendar"/>) in a configured time zone.</summary>
public sealed class PersianChronoAuditCalendar : IChronoAuditCalendar
{
    private readonly TimeZoneInfo _timeZone;
    private readonly PersianCalendar _calendar = new();

    /// <summary>Creates the calendar.</summary>
    /// <param name="timeZone">The time zone in which the day boundaries are taken.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeZone"/> is null.</exception>
    public PersianChronoAuditCalendar(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        _timeZone = timeZone;
    }

    /// <inheritdoc />
    public string Name => "Persian";

    /// <inheritdoc />
    public CalendarParts PartsOf(DateTimeOffset instantUtc)
    {
        // Convert first: the day boundary is the one of the configured zone, not of UTC.
        var local = TimeZoneInfo.ConvertTime(instantUtc, _timeZone).DateTime;
        var month = _calendar.GetMonth(local);
        return new CalendarParts(_calendar.GetYear(local), month, _calendar.GetDayOfMonth(local), (month - 1) / 3 + 1);
    }

    /// <inheritdoc />
    public string FormatDay(int year, int month, int day) =>
        string.Create(CultureInfo.InvariantCulture, $"{year:D4}/{month:D2}/{day:D2}");
}
