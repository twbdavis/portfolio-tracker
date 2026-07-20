namespace PortfolioTracker.Core;

public static class MarketHours
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// US regular session: 9:30 AM - 4:00 PM ET, Mon-Fri. No holiday calendar in v1.
    public static bool IsOpen(DateTimeOffset utcNow)
    {
        var et = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var t = et.TimeOfDay;
        return t >= new TimeSpan(9, 30, 0) && t < new TimeSpan(16, 0, 0);
    }

    /// The ET calendar date a quote fetched now belongs to, or null on
    /// weekends / before the open — a pre-market Monday quote is really
    /// Friday's close and must not be stamped as a Monday DailyClose.
    public static DateOnly? TradingDateForSnapshot(DateTimeOffset utcNow)
    {
        var et = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        if (et.TimeOfDay < new TimeSpan(9, 30, 0)) return null;
        return DateOnly.FromDateTime(et.Date);
    }

    /// True after the regular session has closed on an ET weekday — the
    /// crawler's default window, when a live quote IS the day's close.
    public static bool IsAfterClose(DateTimeOffset utcNow)
    {
        var et = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        return et.TimeOfDay >= new TimeSpan(16, 30, 0);
    }

    /// The most recent ET weekday whose session has already completed —
    /// history is "caught up" when DailyClose reaches this date. Ignores
    /// holidays like everything else here; a holiday just looks like one
    /// permanently-stale append attempt per day, bounded and harmless.
    public static DateOnly MostRecentCompletedTradingDay(DateTimeOffset utcNow)
    {
        var et = TimeZoneInfo.ConvertTime(utcNow, Eastern);
        var date = DateOnly.FromDateTime(et.Date);
        if (et.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
            && et.TimeOfDay >= new TimeSpan(16, 0, 0))
            return date;
        do { date = date.AddDays(-1); }
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return date;
    }
}
