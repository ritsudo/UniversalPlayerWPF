using Microsoft.Data.Sqlite;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Data;

public sealed class ArtistRepository
{
    private readonly Database _db;
    public ArtistRepository(Database db) => _db = db;

    public Artist? Get(string name)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, genre, genre_source, updated_utc FROM artists WHERE name = @n LIMIT 1;";
        cmd.Parameters.AddWithValue("@n", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Artist
        {
            Name = r.GetString(0),
            Genre = DbValue.GetStringOrNull(r, 1),
            GenreSource = Enum.TryParse<GenreSource>(DbValue.GetStringOrNull(r, 2), out var gs) ? gs : GenreSource.None,
            UpdatedUtc = DbValue.GetNullableDate(r, 3)
        };
    }

    public void Upsert(string name, string? genre, GenreSource source)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        using var c = _db.Open();
        Upsert(c, null, name, genre, source);
    }

    public void Upsert(SqliteConnection conn, SqliteTransaction? tx, string name, string? genre, GenreSource source)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO artists(name, genre, genre_source, updated_utc)
            VALUES(@n, @g, @s, @u)
            ON CONFLICT(name) DO UPDATE SET
                genre        = CASE WHEN excluded.genre IS NULL THEN artists.genre ELSE excluded.genre END,
                genre_source = CASE WHEN excluded.genre IS NULL THEN artists.genre_source ELSE excluded.genre_source END,
                updated_utc  = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@g", (object?)genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", source.ToString());
        cmd.Parameters.AddWithValue("@u", DbValue.ToDb(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    public List<Artist> GetAllWithCounts()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.artist AS name,
                   COALESCE(a.genre,
                            (SELECT x.genre FROM tracks x
                             WHERE x.artist = t.artist AND x.genre IS NOT NULL AND x.missing = 0
                             GROUP BY x.genre ORDER BY COUNT(*) DESC LIMIT 1)) AS genre,
                   COALESCE(a.genre_source, 'None') AS genre_source,
                   a.updated_utc,
                   COUNT(*) AS tracks,
                   COALESCE(SUM(t.play_count), 0) AS plays
            FROM tracks t
            LEFT JOIN artists a ON a.name = t.artist
            WHERE t.missing = 0 AND t.artist IS NOT NULL AND t.artist <> ''
            GROUP BY t.artist
            ORDER BY tracks DESC, name;
            """;
        var list = new List<Artist>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Artist
            {
                Name = r.GetString(0),
                Genre = DbValue.GetStringOrNull(r, 1),
                GenreSource = Enum.TryParse<GenreSource>(DbValue.GetStringOrNull(r, 2), out var gs) ? gs : GenreSource.None,
                UpdatedUtc = DbValue.GetNullableDate(r, 3),
                TrackCount = r.GetInt32(4),
                PlayCount = r.GetInt32(5)
            });
        }
        return list;
    }
}
