using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Research;

namespace PortfolioTracker.Core.Tests;

public class ScoreboardMathTests
{
    private static SignalHit Hit(long id, string kind, string ticker = "MU", bool dismissed = false) => new()
    {
        Id = id, Ticker = ticker, SignalKind = kind, InputsJson = "{}",
        FiredAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), Dismissed = dismissed,
    };

    private static SignalOutcomeRow Outcome(long hitId, string kind, decimal? e30 = null,
        decimal? e60 = null, decimal? e90 = null, string ticker = "MU") => new()
    {
        SignalHitId = hitId, Ticker = ticker, SignalKind = kind,
        FireDate = new DateOnly(2026, 5, 1),
        Excess30 = e30, Excess60 = e60, Excess90 = e90,
    };

    [Fact]
    public void Scoreboard_reports_fires_matured_median_beatrate_and_examples()
    {
        var hits = new List<SignalHit> { Hit(1, "CheapGrowth"), Hit(2, "CheapGrowth"), Hit(3, "CheapGrowth") };
        var outcomes = new List<SignalOutcomeRow>
        {
            Outcome(1, "CheapGrowth", e30: 5m, ticker: "WIN"),
            Outcome(2, "CheapGrowth", e30: -2m, ticker: "LOSE"),
            Outcome(3, "CheapGrowth"),   // not matured yet
        };
        var rows = ScoreboardMath.Build(hits, outcomes, downWeightingEnabled: false);
        var row = Assert.Single(rows);
        var h30 = row.Horizons.Single(h => h.Days == 30);

        Assert.Equal(3, row.Fires);
        Assert.Equal(2, h30.Matured);
        Assert.Equal(1.5m, h30.MedianExcess);
        Assert.Equal(50m, h30.BeatRatePct);
        Assert.Equal("WIN", h30.Best!.Ticker);
        Assert.Equal("LOSE", h30.Worst!.Ticker);
        Assert.False(h30.Sufficient);   // 2 < 10 -> "insufficient data"
    }

    [Fact]
    public void Sufficiency_flag_flips_at_ten_matured_observations()
    {
        var hits = Enumerable.Range(1, 10).Select(i => Hit(i, "GoldenCross")).ToList();
        var outcomes = Enumerable.Range(1, 10)
            .Select(i => Outcome(i, "GoldenCross", e30: i)).ToList();
        var row = Assert.Single(ScoreboardMath.Build(hits, outcomes, false));
        Assert.True(row.Horizons.Single(h => h.Days == 30).Sufficient);
    }

    [Fact]
    public void Dismiss_rate_tracks_user_perceived_noise()
    {
        var hits = new List<SignalHit>
        {
            Hit(1, "DeathCross", dismissed: true),
            Hit(2, "DeathCross", dismissed: true),
            Hit(3, "DeathCross"),
            Hit(4, "DeathCross"),
        };
        var row = Assert.Single(ScoreboardMath.Build(hits, new List<SignalOutcomeRow>(), false));
        Assert.Equal(50m, row.DismissRatePct);
    }

    [Fact]
    public void Down_weighting_requires_twenty_observations_and_a_sub_45_beat_rate()
    {
        List<SignalOutcomeRow> Outcomes(int count, int beats) =>
            Enumerable.Range(1, count)
                .Select(i => Outcome(i, "K", e60: i <= beats ? 1m : -1m)).ToList();
        List<SignalHit> Hits(int count) => Enumerable.Range(1, count).Select(i => Hit(i, "K")).ToList();

        // 19 obs at 0% beat rate: not enough observations
        var thin = Assert.Single(ScoreboardMath.Build(Hits(19), Outcomes(19, 0), true));
        Assert.False(thin.DownWeighted);

        // 20 obs, 8 beats = 40% < 45%: flagged, with the reason
        var flagged = Assert.Single(ScoreboardMath.Build(Hits(20), Outcomes(20, 8), true));
        Assert.True(flagged.DownWeighted);
        Assert.Contains("40%", flagged.DownWeightReason);

        // 20 obs, 9 beats = 45%: at the floor, not below -> not flagged
        var atFloor = Assert.Single(ScoreboardMath.Build(Hits(20), Outcomes(20, 9), true));
        Assert.False(atFloor.DownWeighted);

        // same stats with the setting off: never flagged
        var off = Assert.Single(ScoreboardMath.Build(Hits(20), Outcomes(20, 8), false));
        Assert.False(off.DownWeighted);
        Assert.Empty(ScoreboardMath.DownWeightedKinds(new List<ScoreboardRow> { off }, enabled: false));

        var map = ScoreboardMath.DownWeightedKinds(new List<ScoreboardRow> { flagged }, enabled: true);
        Assert.True(map.ContainsKey("K"));
    }
}
