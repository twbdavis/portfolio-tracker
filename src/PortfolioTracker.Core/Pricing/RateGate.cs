namespace PortfolioTracker.Core.Pricing;

/// Token-bucket limiter shared across services so quote refreshes and research
/// lookups draw from one Finnhub free-tier budget (60 calls/min). Up to `burst`
/// calls proceed immediately; tokens refill one per `minInterval`, keeping the
/// sustained rate under the budget while a refresh pass fans out in parallel.
/// burst=1 degenerates to the old fixed-spacing behavior.
public class RateGate(TimeSpan minInterval, int burst = 1)
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    private double _tokens = burst;
    private DateTimeOffset _lastRefill = DateTimeOffset.Now;

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            await _sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.Now;
                _tokens = minInterval > TimeSpan.Zero
                    ? Math.Min(burst, _tokens + (now - _lastRefill).Ticks / (double)minInterval.Ticks)
                    : burst;
                _lastRefill = now;
                if (_tokens >= 1) { _tokens -= 1; return; }
                wait = minInterval * (1 - _tokens);
            }
            finally { _sem.Release(); }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
