using System.Net.Http.Json;
using System.Text.Json;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Api.Tests;

/// Tax dashboard + Form 8949 export + sell simulator over the live host.
/// Seeds via Core (like the WinForms app would after imports), asserts HTTP.
public class TaxSimulatorTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fx;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private readonly DateOnly _sellDate;    // MU gain sale, safely outside the 30-day wash window
    private readonly int _year;
    private readonly long _accountId;

    public TaxSimulatorTests(ApiFixture fx)
    {
        _fx = fx;
        _sellDate = _today.AddDays(-60);
        _year = _sellDate.Year;
        _accountId = Seed();
    }

    /// Idempotent: the fixture is shared across the tests in this class.
    /// Prior year: AAA sold at a -5,000 ST loss => computed carry-in ST 2,000
    /// (3,000 absorbed by the ordinary-income offset). Sale year: MU +500 ST.
    /// Open NVDA lot (10 sh @ $100) for the simulator.
    private long Seed()
    {
        var core = _fx.Host.Core;
        var existing = core.Accounts.GetAll().FirstOrDefault(a => a.DisplayName == "Tax Test");
        if (existing is not null) return existing.Id;

        var account = new Account
        {
            AccountNumber = "...2222", DisplayName = "Tax Test", CreatedAt = DateTimeOffset.UtcNow,
        };
        core.Accounts.Insert(account);

        void Tx(string ticker, DateOnly date, TxType type, decimal shares, decimal amount) =>
            core.Transactions.Insert(new Transaction
            {
                AccountId = account.Id, Ticker = ticker, TradeDate = date,
                Type = type, Shares = shares, Amount = amount, Source = LotSource.Manual,
            });

        core.ManualEntry.AddLot(account.Id, "AAA", 10m, 6000m, new DateOnly(_year - 1, 1, 5), false);
        Tx("AAA", new DateOnly(_year - 1, 6, 1), TxType.Sell, 10m, 1000m);   // -5,000 ST loss
        core.ManualEntry.AddLot(account.Id, "MU", 10m, 1000m, _sellDate.AddDays(-90), false);
        Tx("MU", _sellDate, TxType.Sell, 10m, 1500m);                        // +500 ST gain
        core.ManualEntry.AddLot(account.Id, "NVDA", 10m, 1000m, _today.AddDays(-120), false);
        core.Tax.RebuildAll();
        return account.Id;
    }

    [Fact]
    public async Task Year_data_reports_realized_event_and_computed_carry_in()
    {
        var data = await _fx.Client.GetFromJsonAsync<JsonElement>($"/api/tax/{_year}");

        Assert.Equal(_year, data.GetProperty("year").GetInt32());
        Assert.Equal(500m, data.GetProperty("yearShortTerm").GetDecimal());
        Assert.Equal(0m, data.GetProperty("yearLongTerm").GetDecimal());
        Assert.Equal(2000m, data.GetProperty("carryInShortTerm").GetDecimal());
        Assert.False(data.GetProperty("carryInIsManual").GetBoolean());
        Assert.Equal(0m, data.GetProperty("totalDisallowed").GetDecimal());
        Assert.Equal(0m, data.GetProperty("permanentlyLost").GetDecimal());
        Assert.Contains(_year, data.GetProperty("availableYears").EnumerateArray().Select(y => y.GetInt32()));
        Assert.Contains(_year - 1, data.GetProperty("availableYears").EnumerateArray().Select(y => y.GetInt32()));

        var mu = data.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("ticker").GetString() == "MU");
        Assert.Equal("Tax Test", mu.GetProperty("account").GetString());
        Assert.Equal(10m, mu.GetProperty("shares").GetDecimal());
        Assert.Equal(1500m, mu.GetProperty("proceeds").GetDecimal());
        Assert.Equal(1000m, mu.GetProperty("basis").GetDecimal());
        Assert.Equal(500m, mu.GetProperty("gainLoss").GetDecimal());
        Assert.Equal("ShortTerm", mu.GetProperty("term").GetString());
        Assert.False(mu.GetProperty("isWash").GetBoolean());

        // 500 gain against a 2,000 carry-in: net -1,500, fully offset, nothing taxable
        var netting = data.GetProperty("netting");
        Assert.Equal(-1500m, netting.GetProperty("netShortTerm").GetDecimal());
        Assert.Equal(0m, netting.GetProperty("taxableShortTerm").GetDecimal());
        Assert.Equal(1500m, netting.GetProperty("ordinaryOffset").GetDecimal());
    }

    [Fact]
    public async Task Form8949_csv_downloads_with_expected_header()
    {
        var resp = await _fx.Client.GetAsync($"/api/tax/{_year}/8949.csv");
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"form8949-{_year}.csv", resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var csv = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith($"Form 8949-style export,Tax year {_year},ESTIMATES ONLY", csv);
        Assert.Contains("SHORT-TERM", csv);
        Assert.Contains("Description,Account,Date Acquired,Date Sold,Proceeds,Cost Basis,Adjustment Code,Adjustment Amount,Gain/Loss", csv);
        Assert.Contains("10 sh MU", csv);
    }

    [Fact]
    public async Task Manual_carryforward_round_trips_and_clears()
    {
        // the carry INTO _year is the prior year's row, like CarryforwardForm
        var patch = await _fx.Client.PatchAsJsonAsync($"/api/tax/{_year - 1}/carryforward",
            new { shortTerm = 1234m, longTerm = 55m });
        patch.EnsureSuccessStatusCode();

        var data = await _fx.Client.GetFromJsonAsync<JsonElement>($"/api/tax/{_year}");
        Assert.Equal(1234m, data.GetProperty("carryInShortTerm").GetDecimal());
        Assert.Equal(55m, data.GetProperty("carryInLongTerm").GetDecimal());
        Assert.True(data.GetProperty("carryInIsManual").GetBoolean());

        var del = await _fx.Client.DeleteAsync($"/api/tax/{_year - 1}/carryforward");
        del.EnsureSuccessStatusCode();

        data = await _fx.Client.GetFromJsonAsync<JsonElement>($"/api/tax/{_year}");
        Assert.Equal(2000m, data.GetProperty("carryInShortTerm").GetDecimal());
        Assert.False(data.GetProperty("carryInIsManual").GetBoolean());

        var negative = await _fx.Client.PatchAsJsonAsync($"/api/tax/{_year - 1}/carryforward",
            new { shortTerm = -1m, longTerm = 0m });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task Simulator_previews_consumption_and_term_split()
    {
        var resp = await _fx.Client.PostAsJsonAsync("/api/simulator/sell",
            new { accountId = _accountId, ticker = "NVDA", shares = 5m, price = 200m });
        resp.EnsureSuccessStatusCode();
        var sim = await resp.Content.ReadFromJsonAsync<JsonElement>();

        var portion = Assert.Single(sim.GetProperty("portions").EnumerateArray());
        Assert.Equal(5m, portion.GetProperty("shares").GetDecimal());
        Assert.Equal("ShortTerm", portion.GetProperty("term").GetString());
        Assert.Equal(500m, portion.GetProperty("basis").GetDecimal());
        Assert.Equal(1000m, portion.GetProperty("proceeds").GetDecimal());
        Assert.Equal(500m, portion.GetProperty("gainLoss").GetDecimal());

        Assert.Equal(500m, sim.GetProperty("shortTermGainLoss").GetDecimal());
        Assert.Equal(0m, sim.GetProperty("longTermGainLoss").GetDecimal());
        Assert.Equal(1000m, sim.GetProperty("totalProceeds").GetDecimal());
        Assert.Equal(5m, sim.GetProperty("sharesRequested").GetDecimal());
        Assert.Equal(10m, sim.GetProperty("sharesAvailable").GetDecimal());
        Assert.Equal(JsonValueKind.Null, sim.GetProperty("iraWashWarning").ValueKind);

        // a short-term gain portion always yields a days-to-long-term callout
        var callout = Assert.Single(sim.GetProperty("longTermCallouts").EnumerateArray());
        Assert.Equal(5m, callout.GetProperty("shares").GetDecimal());
        Assert.True(callout.GetProperty("days").GetInt32() > 0);
    }

    [Fact]
    public async Task Simulator_rejects_bad_input()
    {
        var noShares = await _fx.Client.PostAsJsonAsync("/api/simulator/sell",
            new { accountId = _accountId, ticker = "NVDA", shares = 0m, price = 200m });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, noShares.StatusCode);

        var noPosition = await _fx.Client.PostAsJsonAsync("/api/simulator/sell",
            new { accountId = _accountId, ticker = "ZZZZ", shares = 1m, price = 1m });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, noPosition.StatusCode);
    }
}
