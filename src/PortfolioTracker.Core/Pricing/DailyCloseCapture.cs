using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

/// Going-forward close capture: every live quote the app sees is stamped as a
/// LiveSnapshot DailyClose for its ET trading date. Intraday writes are an
/// approximation that converges on the real close as later quotes overwrite
/// them; quotes fetched after the bell (the crawler's window) ARE the close.
/// Stooq rows always win over these (see DailyCloseRepository precedence).
public static class DailyCloseCapture
{
    public static void Capture(DailyCloseRepository closes, IEnumerable<PriceQuote> quotes)
    {
        foreach (var q in quotes)
        {
            if (q.Price <= 0m) continue;
            if (MarketHours.TradingDateForSnapshot(q.FetchedAt.ToUniversalTime()) is not DateOnly date) continue;
            closes.UpsertMany(q.Ticker, new[]
            {
                new DailyCloseRow { Ticker = q.Ticker, Date = date, Close = q.Price, Source = CloseSource.LiveSnapshot },
            });
        }
    }
}
