using System.Globalization;
using Microsoft.Data.Sqlite;

namespace UniversalPlayer.Core.Data;

/// <summary>Compact conversion helpers between CLR values and SQLite columns.</summary>
internal static class DbValue
{
    private const string Format = "yyyy-MM-dd HH:mm:ss.fff";

    public static object ToDb(DateTime value) => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
    public static object ToDb(DateTime? value) => value.HasValue ? ToDb(value.Value) : DBNull.Value;

    public static DateTime GetDate(SqliteDataReader r, int i) =>
        DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : DateTime.UnixEpoch;

    public static DateTime? GetNullableDate(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : GetDate(r, i);

    public static string? GetStringOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static int? GetIntOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static long? GetLongOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
