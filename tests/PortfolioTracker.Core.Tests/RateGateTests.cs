using System.Diagnostics;
using PortfolioTracker.Core.Pricing;

namespace PortfolioTracker.Core.Tests;

public class RateGateTests
{
    [Fact]
    public async Task Burst_passes_immediately_then_paces_at_the_refill_rate()
    {
        var gate = new RateGate(TimeSpan.FromMilliseconds(400), burst: 3);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) await gate.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 250, $"burst should not wait, took {sw.ElapsedMilliseconds}ms");
        await gate.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 300, $"4th call should wait for a refill, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Default_burst_of_one_keeps_the_old_fixed_spacing()
    {
        var gate = new RateGate(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"second call should be spaced, took {sw.ElapsedMilliseconds}ms");
    }
}
