namespace ChronoAudit.Core.Tests;

public class CalendarTests
{
    private static TimeZoneInfo Tehran()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Tehran", out var zone) ||
            TimeZoneInfo.TryFindSystemTimeZoneById("Iran Standard Time", out zone))
        {
            return zone;
        }

        // Iran has no daylight saving time since 2022, so a fixed offset is the same zone for 2026 instants.
        return TimeZoneInfo.CreateCustomTimeZone("test-tehran", new TimeSpan(3, 30, 0), "Tehran", "Tehran");
    }

    [Fact]
    public void Persian_FirstDayOfTheYear_1405()
    {
        var calendar = new PersianChronoAuditCalendar(Tehran());

        var parts = calendar.PartsOf(new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new CalendarParts(1405, 1, 1, 1), parts);
        Assert.Equal("Persian", calendar.Name);
    }

    [Fact]
    public void Persian_LateEveningUtc_MovesToTheNextLocalDay()
    {
        var calendar = new PersianChronoAuditCalendar(Tehran());

        // 22:00 UTC is 01:30 the next day at UTC+3:30.
        var parts = calendar.PartsOf(new DateTimeOffset(2026, 3, 21, 22, 0, 0, TimeSpan.Zero));

        Assert.Equal(new CalendarParts(1405, 1, 2, 1), parts);
    }

    [Fact]
    public void Persian_QuarterFollowsTheMonth()
    {
        var calendar = new PersianChronoAuditCalendar(TimeZoneInfo.Utc);

        // 2026-07-15 is 1405/04/24: month 4 is in quarter 2.
        var parts = calendar.PartsOf(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new CalendarParts(1405, 4, 24, 2), parts);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    [InlineData(12, 4)]
    public void Gregorian_QuarterIsMonthDividedByThree(int month, int quarter)
    {
        var calendar = new GregorianChronoAuditCalendar(TimeZoneInfo.Utc);

        var parts = calendar.PartsOf(new DateTimeOffset(2026, month, 10, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(new CalendarParts(2026, month, 10, quarter), parts);
        Assert.Equal("Gregorian", calendar.Name);
    }

    [Fact]
    public void Gregorian_UsesTheConfiguredZoneForTheDayBoundary()
    {
        var plusThreeThirty = TimeZoneInfo.CreateCustomTimeZone("plus330", new TimeSpan(3, 30, 0), "plus330", "plus330");
        var calendar = new GregorianChronoAuditCalendar(plusThreeThirty);

        var parts = calendar.PartsOf(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero));

        Assert.Equal(new CalendarParts(2027, 1, 1, 1), parts);
    }

    [Fact]
    public void FormatDay_PadsWithZeros()
    {
        var gregorian = new GregorianChronoAuditCalendar(TimeZoneInfo.Utc);
        var persian = new PersianChronoAuditCalendar(TimeZoneInfo.Utc);

        Assert.Equal("2026/11/01", gregorian.FormatDay(2026, 11, 1));
        Assert.Equal("1405/01/02", persian.FormatDay(1405, 1, 2));
    }

    [Fact]
    public void Constructors_RejectANullZone()
    {
        Assert.Throws<ArgumentNullException>(() => new GregorianChronoAuditCalendar(null!));
        Assert.Throws<ArgumentNullException>(() => new PersianChronoAuditCalendar(null!));
    }
}
