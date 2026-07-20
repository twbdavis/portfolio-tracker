using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Pricing;

public record PriceRefreshResult(Dictionary<string, PriceQuote> Quotes, List<string> Unresolved, string? Error);

/// Fetches quotes in parallel waves. Every call still draws a token from the
/// shared RateGate, so the sustained rate stays under Finnhub's free-tier
/// 60 calls/min, but a pass fans out instead of pacing 1.1s per ticker.
/// Each wave is cached in one transaction and reported via onProgress so
/// callers can stream prices to the UI as they arrive.
public class PriceService(IPriceProvider provider, PriceCacheRepository cache,
    TimeSpan? minInterval = null, RateGate? gate = null, int waveSize = 10)
{
    private readonly RateGate _gate =
        gate ?? new RateGate(minInterval ?? TimeSpan.FromMilliseconds(1100), burst: 50);

    public async Task<PriceRefreshResult> RefreshAsync(IReadOnlyList<string> tickers, CancellationToken ct,
        Func<IReadOnlyList<PriceQuote>, Task>? onProgress = null)
    {
        var quotes = new Dictionary<string, PriceQuote>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        string? error = null;
        var cancelled = false;
        for (var start = 0; start < tickers.Count && error is null && !cancelled; start += waveSize)
        {
            var wave = tickers.Skip(start).Take(waveSize).ToList();
            var tasks = wave.Select(t => FetchAsync(t, ct)).ToList();
            var fetched = new List<PriceQuote>();
            for (var i = 0; i < wave.Count; i++)
            {
                try
                {
                    var q = await tasks[i].ConfigureAwait(false);
                    if (q is null) unresolved.Add(wave[i]);
                    else { fetched.Add(q); quotes[q.Ticker] = q; }
                }
                catch (PriceFetchException ex) { error ??= ex.Message; } // one status-bar message; keep what we have
                catch (OperationCanceledException) { cancelled = true; }
            }
            if (fetched.Count > 0)
            {
                cache.UpsertMany(fetched);
                if (onProgress is not null) await onProgress(fetched).ConfigureAwait(false);
            }
        }
        return new PriceRefreshResult(quotes, unresolved, error);
    }

    private async Task<PriceQuote?> FetchAsync(string ticker, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return await provider.GetQuoteAsync(ticker, ct).ConfigureAwait(false);
    }
}
