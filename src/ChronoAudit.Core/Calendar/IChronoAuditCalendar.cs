namespace ChronoAudit;

/// <summary>
/// Turns a UTC instant into calendar parts. Rows store these parts at write time, so time grains of the analytics
/// engine group by the configured calendar and no query ever reads a member of a <see cref="DateTimeOffset"/>.
/// </summary>
public interface IChronoAuditCalendar
{
    /// <summary>The calendar name: <c>Gregorian</c>, <c>Persian</c> or a host name.</summary>
    string Name { get; }

    /// <summary>The calendar parts of an instant, taken in the configured time zone.</summary>
    /// <param name="instantUtc">The instant.</param>
    /// <returns>The parts.</returns>
    CalendarParts PartsOf(DateTimeOffset instantUtc);

    /// <summary>Formats a calendar day as <c>yyyy/MM/dd</c>.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <returns>The text, for example <c>2026/11/01</c>.</returns>
    string FormatDay(int year, int month, int day);
}
