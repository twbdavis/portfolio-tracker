using PortfolioTracker.Core.Data;
using PortfolioTracker.Core.Models;
using PortfolioTracker.Core.Portfolio;

namespace PortfolioTracker.Core.Tax;

public class TaxYearEvent
{
    public required string Ticker { get; init; }
    public required ConsumptionView View { get; init; }
    public required long AccountId { get; init; }
    public required string Account { get; init; }    // display name
}

/// A ticker with a recent realized loss that a repurchase would wash.
public record WashRisk(string Ticker, decimal LossAtRisk, DateOnly LatestSale, DateOnly SafeToBuyOn);

/// A taxable holding whose loss sale would be PERMANENTLY destroyed by IRA
/// purchases of the same ticker inside the 61-day window (Rev. Rul. 2008-5).
public record IraWashRisk(string Ticker, string IraAccount, List<DateOnly> RecentIraBuys,
    bool Recurring, DateOnly SafeToSellOn, string Warning);

public class TaxYearData
{
    public int Year { get; init; }
    public List<TaxYearEvent> Events { get; init; } = new();
    public decimal YearShortTerm { get; set; }           // wash-adjusted, before carry-in
    public decimal YearLongTerm { get; set; }
    public decimal CarryInShortTerm { get; set; }        // positive = loss carried in
    public decimal CarryInLongTerm { get; set; }
    public bool CarryInIsManual { get; set; }
    public NettingResult Netting { get; set; } = null!;
    public decimal TotalDisallowed { get; set; }
    public decimal PermanentlyLost { get; set; }         // IRA washes: deduction destroyed forever
    public decimal GrossDividends { get; set; }
    public List<(string Ticker, decimal Amount)> DividendsByTicker { get; set; } = new();
    public TaxEstimate Estimate { get; set; } = null!;
    public bool BracketsAreFallback { get; set; }        // no bracket data for this exact year
    public int RetirementAccountsExcluded { get; set; }  // for the "Excludes N retirement account(s)" line
}

/// Read-side of the tax module. IRA accounts are invisible to the tax math —
/// their dividends and sales are tracked for portfolio purposes but are never
/// taxable events — while their PURCHASES still participate in wash matching.
/// Wash-adjusted views are always recomputed live from raw lots + consumptions
/// so displayed numbers can never drift from the persisted WashSaleAdjustment
/// rows (which Rebuilder refreshes on every change).
public class TaxService
{
    private readonly AccountRepository _accounts;
    private readonly PositionRepository _positions;
    private readonly TransactionRepository _txs;
    private readonly LotRepository _lots;
    private readonly LotConsumptionRepository _consumptions;
    private readonly CashFlowRepository _cash;
    private readonly TaxCarryforwardRepository _carryforwards;
    public TaxSettingsRepository Settings { get; }
    private readonly Rebuilder _rebuilder;

    public TaxService(Database db)
    {
        _accounts = new AccountRepository(db);
        _positions = new PositionRepository(db);
        _txs = new TransactionRepository(db);
        _lots = new LotRepository(db);
        _consumptions = new LotConsumptionRepository(db);
        _cash = new CashFlowRepository(db);
        _carryforwards = new TaxCarryforwardRepository(db);
        Settings = new TaxSettingsRepository(db);
        _rebuilder = new Rebuilder(_accounts, _positions, _txs, _lots, new SaleRepository(db),
            _consumptions, new WashSaleAdjustmentRepository(db));
    }

    /// Wash-adjusted consumption views for every TAXABLE position, tagged with
    /// ticker and account. Wash matching itself spans all accounts including
    /// IRAs. Bulk-loads lots and consumptions in two queries instead of two per
    /// position. Null filter = all taxable accounts.
    public List<TaxYearEvent> GetAllEvents(long? accountFilter = null)
    {
        var accounts = _accounts.GetAll().ToDictionary(a => a.Id);
        var events = new List<TaxYearEvent>();
        var lotsByPosition = _lots.GetAll().ToLookup(l => l.PositionId);
        var consByPosition = _consumptions.GetAll().ToLookup(c => c.PositionId);

        foreach (var group in _positions.GetAll().GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase))
        {
            var positions = group.ToList();
            var cons = positions.SelectMany(p => consByPosition[p.Id]).ToList();
            if (cons.Count == 0) continue;
            var scope = positions.ToDictionary(p => p.Id,
                p => new PositionTaxScope(p.AccountId,
                    accounts.TryGetValue(p.AccountId, out var a) ? a.TaxTreatment : TaxTreatment.Taxable));
            var result = WashSaleEngine.Compute(
                positions.SelectMany(p => lotsByPosition[p.Id]).ToList(), cons, scope, DateTimeOffset.Now);
            var accountOf = positions.ToDictionary(p => p.Id, p => accounts[p.AccountId]);
            events.AddRange(result.Views
                .Select(v => (View: v, Account: accountOf[v.Source.PositionId]))
                .Where(x => !x.Account.IsRetirement)
                .Where(x => accountFilter is null || x.Account.Id == accountFilter)
                .Select(x => new TaxYearEvent
                {
                    Ticker = group.First().Ticker.ToUpperInvariant(),
                    View = x.View,
                    AccountId = x.Account.Id,
                    Account = x.Account.DisplayName,
                }));
        }
        return events;
    }

    /// Cross-account wash engine result for one ticker (open-lot states feed
    /// the simulator and the per-lot detail view).
    public WashSaleResult ComputeForTicker(string ticker)
    {
        var accounts = _accounts.GetAll().ToDictionary(a => a.Id);
        var positions = _positions.GetAllByTicker(ticker);
        var scope = positions.ToDictionary(p => p.Id,
            p => new PositionTaxScope(p.AccountId,
                accounts.TryGetValue(p.AccountId, out var a) ? a.TaxTreatment : TaxTreatment.Taxable));
        return WashSaleEngine.Compute(
            positions.SelectMany(p => _lots.GetByPosition(p.Id, openOnly: false)).ToList(),
            positions.SelectMany(p => _consumptions.GetByPosition(p.Id)).ToList(),
            scope, DateTimeOffset.Now);
    }

    /// Same engine run scoped by the ticker that owns this position — states
    /// for other accounts' lots ride along and are simply unused by callers.
    public WashSaleResult ComputeForPosition(long positionId)
    {
        var pos = _positions.GetAll().First(p => p.Id == positionId);
        return ComputeForTicker(pos.Ticker);
    }

    public TaxYearData GetYearData(int year, TaxBracketsFile brackets, long? accountFilter = null)
    {
        var all = GetAllEvents(accountFilter);
        // capital-loss carryforward is taxpayer-level: computed and applied only
        // for the all-accounts view so a single-account filter can't corrupt it
        var (carryIn, manual) = accountFilter is null
            ? ComputeCarryforwardChain(all, year)
            : ((0m, 0m), false);
        var events = all.Where(e => e.View.Source.SaleTradeDate.Year == year)
            .OrderBy(e => e.View.Source.SaleTradeDate).ThenBy(e => e.View.Source.Id).ToList();

        var yearSt = events.Where(e => e.View.EffectiveTerm == HoldingTerm.ShortTerm).Sum(e => e.View.AdjustedGainLoss);
        var yearLt = events.Where(e => e.View.EffectiveTerm == HoldingTerm.LongTerm).Sum(e => e.View.AdjustedGainLoss);
        var netting = TaxEstimator.Net(yearSt - carryIn.Item1, yearLt - carryIn.Item2);

        // persist this year's computed carry-out so next year picks it up (manual rows win)
        if (accountFilter is null)
            _carryforwards.Upsert(new TaxCarryforward
            {
                Year = year, ShortTerm = netting.CarryforwardShortTerm,
                LongTerm = netting.CarryforwardLongTerm, IsManual = false,
            });

        var accounts = _accounts.GetAll();
        var taxableIds = accounts.Where(a => !a.IsRetirement).Select(a => a.Id).ToHashSet();
        var dividends = _cash.GetAll()
            .Where(c => c.Type == CashFlowType.Dividend && c.TradeDate.Year == year)
            .Where(c => taxableIds.Contains(c.AccountId))
            .Where(c => accountFilter is null || c.AccountId == accountFilter)
            .GroupBy(c => c.Ticker ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => (Ticker: g.Key, Amount: g.Sum(c => c.Amount)))
            .OrderByDescending(d => d.Amount).ToList();

        var yb = brackets.ForYear(year);
        var estimate = TaxEstimator.Estimate(yb, Settings.GetFilingStatus(), Settings.GetOtherIncome(), netting);

        return new TaxYearData
        {
            Year = year,
            Events = events,
            YearShortTerm = yearSt,
            YearLongTerm = yearLt,
            CarryInShortTerm = carryIn.Item1,
            CarryInLongTerm = carryIn.Item2,
            CarryInIsManual = manual,
            Netting = netting,
            TotalDisallowed = events.Sum(e => e.View.DisallowedLoss),
            PermanentlyLost = events.Sum(e => e.View.IraDisallowed),
            GrossDividends = dividends.Sum(d => d.Amount),
            DividendsByTicker = dividends,
            Estimate = estimate,
            BracketsAreFallback = !brackets.HasYear(year),
            RetirementAccountsExcluded = accounts.Count(a => a.IsRetirement),
        };
    }

    /// Walk years from the first realized event to (year-1), computing each
    /// year's carry-out from its events plus the prior carry-in. A manual row
    /// (edited to match the real return) replaces the computed value for that
    /// year and the chain continues from it.
    private ((decimal, decimal) carryIn, bool manual) ComputeCarryforwardChain(List<TaxYearEvent> all, int year)
    {
        var firstYear = all.Count == 0 ? year : all.Min(e => e.View.Source.SaleTradeDate.Year);
        var stored = _carryforwards.GetAll().ToDictionary(c => c.Year);
        (decimal st, decimal lt) carry = (0m, 0m);
        var manual = false;
        for (var y = firstYear; y < year; y++)
        {
            if (stored.TryGetValue(y, out var row) && row.IsManual)
            {
                carry = (row.ShortTerm, row.LongTerm);
                manual = true;
                continue;
            }
            var st = all.Where(e => e.View.Source.SaleTradeDate.Year == y && e.View.EffectiveTerm == HoldingTerm.ShortTerm)
                .Sum(e => e.View.AdjustedGainLoss);
            var lt = all.Where(e => e.View.Source.SaleTradeDate.Year == y && e.View.EffectiveTerm == HoldingTerm.LongTerm)
                .Sum(e => e.View.AdjustedGainLoss);
            var net = TaxEstimator.Net(st - carry.st, lt - carry.lt);
            carry = (net.CarryforwardShortTerm, net.CarryforwardLongTerm);
            manual = false;
            _carryforwards.Upsert(new TaxCarryforward
            { Year = y, ShortTerm = carry.st, LongTerm = carry.lt, IsManual = false });
        }
        return (carry, manual);
    }

    public void SetManualCarryforward(int year, decimal shortTerm, decimal longTerm) =>
        _carryforwards.Upsert(new TaxCarryforward
        { Year = year, ShortTerm = shortTerm, LongTerm = longTerm, IsManual = true });

    public void ClearManualCarryforward(int year) => _carryforwards.ClearManual(year);

    public TaxCarryforward? GetCarryforward(int year) => _carryforwards.Get(year);

    /// Wash-adjusted realized G/L for the year across taxable accounts, for the
    /// main summary bar.
    public decimal GetRealizedAdjusted(int year) =>
        GetAllEvents().Where(e => e.View.Source.SaleTradeDate.Year == year).Sum(e => e.View.AdjustedGainLoss);

    /// True when derived tax tables are missing but sales exist (first run after
    /// the module was added) — callers should run RebuildAll once.
    public bool NeedsBackfill(SaleRepository sales) => !_consumptions.Any() && sales.GetAll().Count > 0;

    /// Rebuild everything derived (lots, sales, consumptions, wash adjustments)
    /// from raw transactions — the "Rebuild tax data" action. Correct under
    /// multiple accounts: FIFO replays per (account, ticker), wash per ticker.
    public List<string> RebuildAll()
    {
        var rebuilt = new List<string>();
        foreach (var pos in _positions.GetAll())
        {
            if (_txs.GetByAccountAndTicker(pos.AccountId, pos.Ticker).Count == 0) continue;
            _rebuilder.RebuildTicker(pos.AccountId, pos.Ticker);
            rebuilt.Add(pos.Ticker);
        }
        return rebuilt.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Every ticker with an open wash window: a taxable loss realized in the
    /// last 30 days that buying now — in any account, including an IRA — would
    /// disallow. SafeToBuyOn is the first day a repurchase no longer washes.
    public List<WashRisk> GetActiveWashRisks(DateOnly today)
    {
        return GetAllEvents()
            .Where(e => e.View.AdjustedGainLoss < 0m
                        && e.View.Source.SaleTradeDate >= today.AddDays(-30)
                        && e.View.Source.SaleTradeDate <= today)
            .GroupBy(e => e.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latestSale = g.Max(e => e.View.Source.SaleTradeDate);
                return new WashRisk(
                    g.Key.ToUpperInvariant(),
                    -g.Sum(e => e.View.AdjustedGainLoss),
                    latestSale,
                    latestSale.AddDays(31));
            })
            .OrderByDescending(r => r.LossAtRisk).ToList();
    }

    /// Taxable loss sales of this ticker in the 30 days before `today` whose
    /// loss is not yet fully disallowed — buying today (any account) would wash
    /// them. Returns a warning string, or null when buying is clean.
    public string? BuyWashWarning(string ticker, DateOnly today)
    {
        var positions = _positions.GetAllByTicker(ticker.Trim().ToUpperInvariant());
        if (positions.Count == 0) return null;
        var accounts = _accounts.GetAll().ToDictionary(a => a.Id);
        var result = ComputeForTicker(ticker.Trim().ToUpperInvariant());
        var taxablePositions = positions
            .Where(p => accounts.TryGetValue(p.AccountId, out var a) && !a.IsRetirement)
            .Select(p => p.Id).ToHashSet();
        var exposed = result.Views
            .Where(v => taxablePositions.Contains(v.Source.PositionId))
            .Where(v => v.AdjustedGainLoss < 0m
                        && v.Source.SaleTradeDate >= today.AddDays(-30)
                        && v.Source.SaleTradeDate <= today)
            .ToList();
        if (exposed.Count == 0) return null;
        var totalLoss = -exposed.Sum(v => v.AdjustedGainLoss);
        var latest = exposed.Max(v => v.Source.SaleTradeDate);
        var windowEnd = latest.AddDays(30);
        var dates = string.Join(", ", exposed.Select(v => v.Source.SaleTradeDate.ToString("M/d")).Distinct());
        return $"Buying {ticker.ToUpperInvariant()} now disallows the {totalLoss:C2} loss from {dates} — " +
               $"wash sale window ends {windowEnd:M/d}";
    }

    /// The highest-value warning in the app: a contemplated taxable-account
    /// loss sale of this ticker would be PERMANENTLY destroyed by IRA purchases
    /// of the same ticker inside the 61-day window — actual buys already in the
    /// window before the sale, or predicted ones when the IRA buys on a
    /// recurring cadence (Rev. Rul. 2008-5). Null when no IRA holds or buys it.
    public IraWashRisk? GetIraWashRisk(string ticker, DateOnly saleDate, string? sellingAccountLabel = null)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        var iraAccounts = _accounts.GetAll().Where(a => a.IsRetirement).ToDictionary(a => a.Id);
        if (iraAccounts.Count == 0) return null;

        var iraBuys = _txs.GetByTicker(ticker)
            .Where(t => t.Type == TxType.Buy && iraAccounts.ContainsKey(t.AccountId))
            .OrderBy(t => t.TradeDate).ToList();
        if (iraBuys.Count == 0) return null;

        var windowStart = saleDate.AddDays(-30);
        var windowEnd = saleDate.AddDays(30);
        var inWindow = iraBuys.Where(t => t.TradeDate >= windowStart && t.TradeDate <= windowEnd).ToList();

        // recurring cadence: two or more IRA buys in the trailing 90 days with a
        // regular interval predicts the next buy even before it lands
        var recent = iraBuys.Where(t => t.TradeDate >= saleDate.AddDays(-90) && t.TradeDate <= saleDate).ToList();
        DateOnly? predicted = null;
        if (recent.Count >= 2)
        {
            var intervals = recent.Zip(recent.Skip(1), (a, b) => b.TradeDate.DayNumber - a.TradeDate.DayNumber).ToList();
            var typical = intervals.OrderBy(i => i).ElementAt(intervals.Count / 2);
            if (typical > 0 && typical <= 35)
            {
                var next = recent[^1].TradeDate.AddDays(typical);
                if (next > saleDate && next <= windowEnd) predicted = next;
            }
        }
        if (inWindow.Count == 0 && predicted is null) return null;

        var account = iraAccounts[(inWindow.Count > 0 ? inWindow[^1] : recent[^1]).AccountId];
        var lastRelevant = inWindow.Count > 0 ? inWindow[^1].TradeDate : recent[^1].TradeDate;
        var safeOn = (predicted ?? lastRelevant).AddDays(31);
        var buyDates = (inWindow.Count > 0 ? inWindow : recent)
            .Select(t => t.TradeDate).Distinct().ToList();
        var accountLabel = $"{PortfolioCalculator.TreatmentLabel(account.TaxTreatment)} {account.AccountNumber}".Trim();
        var seller = sellingAccountLabel is null ? "a taxable account" : sellingAccountLabel;
        var datesText = string.Join(" and ", buyDates.TakeLast(2).Select(d => d.ToString("M/d")));
        var warning = $"{accountLabel} bought {ticker} on {datesText}" +
            (predicted is DateOnly p ? $" (recurring — next expected ~{p:M/d})" : "") +
            $" — selling {ticker} at a loss in {seller} before ~{safeOn:M/d} permanently destroys the loss (Rev. Rul. 2008-5).";
        return new IraWashRisk(ticker, accountLabel, buyDates, predicted is not null, safeOn, warning);
    }
}
