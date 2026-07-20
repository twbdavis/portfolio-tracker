using PortfolioTracker.Core.Alerts;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Tests;

public class AlertEvaluatorTests
{
    private static TickerTarget T(decimal? target, TargetDirection dir = TargetDirection.AtOrAbove,
        bool enabled = true, DateTimeOffset? triggered = null) => new()
    { Ticker = "NVDA", TargetPrice = target, TargetDirection = dir, AlertEnabled = enabled, AlertTriggeredAt = triggered };

    [Theory]
    [InlineData(195.0, 190.0, true)]    // above target
    [InlineData(190.0, 190.0, true)]    // at target
    [InlineData(189.99, 190.0, false)]  // below target
    public void AtOrAbove_fires_at_or_above(double price, double target, bool expected)
        => Assert.Equal(expected, AlertEvaluator.ShouldFire(T((decimal)target), (decimal)price));

    [Theory]
    [InlineData(88.0, 90.0, true)]      // dipped to target
    [InlineData(90.0, 90.0, true)]
    [InlineData(90.01, 90.0, false)]
    public void AtOrBelow_fires_at_or_below(double price, double target, bool expected)
        => Assert.Equal(expected, AlertEvaluator.ShouldFire(T((decimal)target, TargetDirection.AtOrBelow), (decimal)price));

    [Fact]
    public void Never_fires_when_disabled_or_untargeted_or_already_triggered()
    {
        Assert.False(AlertEvaluator.ShouldFire(T(190m, enabled: false), 195m));
        Assert.False(AlertEvaluator.ShouldFire(T(null), 195m));
        Assert.False(AlertEvaluator.ShouldFire(T(190m, triggered: DateTimeOffset.UtcNow), 195m)); // no re-fire until re-armed
    }
}
