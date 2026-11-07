namespace ChronoAudit;

/// <summary>The calendar parts of an instant in a configured calendar and time zone.</summary>
/// <param name="Year">The year.</param>
/// <param name="Month">The month (1 to 12).</param>
/// <param name="Day">The day of the month.</param>
/// <param name="Quarter">The quarter (1 to 4), <c>(Month - 1) / 3 + 1</c>.</param>
public readonly record struct CalendarParts(int Year, int Month, int Day, int Quarter);
