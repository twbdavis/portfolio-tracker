using Microsoft.Data.Sqlite;
using PortfolioTracker.Core.Models;

namespace PortfolioTracker.Core.Data;

public class TickerMetricsRepository(Database db)
{
    public void Upsert(TickerMetricsRow m)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO TickerMetrics (Ticker, AsOf, MarketCap, PE, PS, PB, RevenueGrowthYoY,
                EpsGrowth, GrossMargin, NetMargin, ROE, Beta, Wk52High, Wk52Low)
            VALUES (@t, @asof, @mc, @pe, @ps, @pb, @rg, @eg, @gm, @nm, @roe, @beta, @hi, @lo)
            ON CONFLICT(Ticker) DO UPDATE SET
                AsOf=excluded.AsOf, MarketCap=excluded.MarketCap, PE=excluded.PE, PS=excluded.PS,
                PB=excluded.PB, RevenueGrowthYoY=excluded.RevenueGrowthYoY, EpsGrowth=excluded.EpsGrowth,
                GrossMargin=excluded.GrossMargin, NetMargin=excluded.NetMargin, ROE=excluded.ROE,
                Beta=excluded.Beta, Wk52High=excluded.Wk52High, Wk52Low=excluded.Wk52Low
            """;
        cmd.Parameters.AddWithValue("@t", m.Ticker.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@asof", Sql.Ts(m.AsOf));
        AddDec(cmd, "@mc", m.MarketCap); AddDec(cmd, "@pe", m.Pe); AddDec(cmd, "@ps", m.Ps);
        AddDec(cmd, "@pb", m.Pb); AddDec(cmd, "@rg", m.RevenueGrowthYoY); AddDec(cmd, "@eg", m.EpsGrowth);
        AddDec(cmd, "@gm", m.GrossMargin); AddDec(cmd, "@nm", m.NetMargin); AddDec(cmd, "@roe", m.Roe);
        AddDec(cmd, "@beta", m.Beta); AddDec(cmd, "@hi", m.Wk52High); AddDec(cmd, "@lo", m.Wk52Low);
        cmd.ExecuteNonQuery();
    }

    public TickerMetricsRow? Get(string ticker)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TickerMetrics WHERE Ticker=@t";
        cmd.Parameters.AddWithValue("@t", ticker);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<TickerMetricsRow> GetAll()
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM TickerMetrics";
        using var r = cmd.ExecuteReader();
        var list = new List<TickerMetricsRow>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static void AddDec(SqliteCommand cmd, string name, decimal? v) =>
        cmd.Parameters.AddWithValue(name, v is decimal d ? Sql.Dec(d) : DBNull.Value);

    private static decimal? Dec(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : Sql.ToDec(r.GetString(i));
    }

    private static TickerMetricsRow Map(SqliteDataReader r) => new()
    {
        Ticker = r.GetString(r.GetOrdinal("Ticker")),
        AsOf = Sql.ToTs(r.GetString(r.GetOrdinal("AsOf"))),
        MarketCap = Dec(r, "MarketCap"),
        Pe = Dec(r, "PE"),
        Ps = Dec(r, "PS"),
        Pb = Dec(r, "PB"),
        RevenueGrowthYoY = Dec(r, "RevenueGrowthYoY"),
        EpsGrowth = Dec(r, "EpsGrowth"),
        GrossMargin = Dec(r, "GrossMargin"),
        NetMargin = Dec(r, "NetMargin"),
        Roe = Dec(r, "ROE"),
        Beta = Dec(r, "Beta"),
        Wk52High = Dec(r, "Wk52High"),
        Wk52Low = Dec(r, "Wk52Low"),
    };
}
