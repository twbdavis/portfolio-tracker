namespace PortfolioTracker.Core.Data;

/// Monthly Anthropic token metering, separate from the Finnhub budget.
public class AiUsageRepository(Database db)
{
    public int GetTokensUsed(string month)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TokensUsed FROM AiUsage WHERE Month=@m";
        cmd.Parameters.AddWithValue("@m", month);
        var v = cmd.ExecuteScalar();
        return v is long l ? (int)l : 0;
    }

    public void AddTokens(string month, int tokens)
    {
        using var conn = db.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AiUsage (Month, TokensUsed) VALUES (@m, @t)
            ON CONFLICT(Month) DO UPDATE SET TokensUsed = TokensUsed + excluded.TokensUsed
            """;
        cmd.Parameters.AddWithValue("@m", month);
        cmd.Parameters.AddWithValue("@t", tokens);
        cmd.ExecuteNonQuery();
    }

    public static string MonthKey(DateTimeOffset at) => at.ToString("yyyy-MM");
}
