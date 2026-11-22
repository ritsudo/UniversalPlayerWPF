namespace UniversalPlayer.Core.Data;

/// <summary>Simple key/value store for scan bookkeeping and assorted flags.</summary>
public sealed class MetaRepository
{
    private readonly Database _db;
    public MetaRepository(Database db) => _db = db;

    public string? Get(string key)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = @k;";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string? value)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta(key, value) VALUES(@k, @v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
