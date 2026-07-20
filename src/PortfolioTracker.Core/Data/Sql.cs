using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PortfolioTracker.Core.Data;

internal static class Sql
{
    public static string Dec(decimal d) => d.ToString(CultureInfo.InvariantCulture);
    public static decimal ToDec(object v) => decimal.Parse((string)v, CultureInfo.InvariantCulture);
    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly ToDate(object v) => DateOnly.ParseExact((string)v, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Ts(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);
    public static DateTimeOffset ToTs(object v) => DateTimeOffset.Parse((string)v, CultureInfo.InvariantCulture);

    public static object OrDbNull(this string? s) => (object?)s ?? DBNull.Value;
    public static object OrDbNull(this long? v) => (object?)v ?? DBNull.Value;
    public static string? AsString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? AsLong(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
