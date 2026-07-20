namespace PortfolioTracker.Core.Pricing;

/// Preemption signal between interactive use and the background crawler: user
/// requests wrap themselves in Begin(), the crawler polls IsQuiet() before
/// every fetch and stalls while a request is in flight or just finished (the
/// cooldown covers the follow-up calls a research page fans out).
public class InteractiveGate
{
    private int _active;
    private long _lastEndTicks;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope(this);
    }

    public bool IsQuiet(TimeSpan cooldown, DateTimeOffset? now = null)
    {
        if (Volatile.Read(ref _active) > 0) return false;
        var last = new DateTimeOffset(Interlocked.Read(ref _lastEndTicks), TimeSpan.Zero);
        return (now ?? DateTimeOffset.UtcNow) - last >= cooldown;
    }

    private void End()
    {
        Interlocked.Exchange(ref _lastEndTicks, DateTimeOffset.UtcNow.Ticks);
        Interlocked.Decrement(ref _active);
    }

    private sealed class Scope(InteractiveGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.End();
        }
    }
}
