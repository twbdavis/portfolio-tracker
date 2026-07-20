using PortfolioTracker.Core;

namespace PortfolioTracker.Core.Tests;

public class MarketHoursTests
{
    // July 2026: EDT = UTC-4. Tue 2026-07-07.
    [Theory]
    [InlineData("2026-07-07T13:30:00Z", true)]   // 9:30 AM ET exactly — open
    [InlineData("2026-07-07T13:29:59Z", false)]  // 9:29:59 AM ET — closed
    [InlineData("2026-07-07T19:59:59Z", true)]   // 3:59:59 PM ET — open
    [InlineData("2026-07-07T20:00:00Z", false)]  // 4:00 PM ET — closed
    [InlineData("2026-07-11T15:00:00Z", false)]  // Saturday — closed
    [InlineData("2026-07-12T15:00:00Z", false)]  // Sunday — closed
    // January 2026: EST = UTC-5. Wed 2026-01-07.
    [InlineData("2026-01-07T14:30:00Z", true)]   // 9:30 AM EST — open (DST-aware)
    [InlineData("2026-01-07T13:30:00Z", false)]  // 8:30 AM EST — closed
    public void IsOpen_respects_eastern_market_hours(string utc, bool expected)
    {
        Assert.Equal(expected, MarketHours.IsOpen(DateTimeOffset.Parse(utc)));
    }
}
