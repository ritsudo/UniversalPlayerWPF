using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Data;

public sealed class HistoryRepository
{
    private readonly Database _db;
    public HistoryRepository(Database db) => _db = db;

    /// <summary>Inserts a listening entry and returns its id.</summary>
    public long Add(HistoryEntry e)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history(track_id, path, title, artist, genre, started_utc, played_seconds)
            VALUES(@tid, @path, @title, @artist, @genre, @started, @sec)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@tid", (object?)e.TrackId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@path", e.Path);
        cmd.Parameters.AddWithValue("@title", (object?)e.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)e.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genre", (object?)e.Genre ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@started", DbValue.ToDb(e.StartedUtc));
        cmd.Parameters.AddWithValue("@sec", e.PlayedSeconds);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void UpdatePlayedSeconds(long id, int seconds)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE history SET played_seconds = @s WHERE id = @id;";
        cmd.Parameters.AddWithValue("@s", seconds);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes entries older than the cutoff (default retention is one month).</summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE started_utc < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", DbValue.ToDb(cutoffUtc));
        return cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history;";
        cmd.ExecuteNonQuery();
    }

    public List<HistoryEntry> GetRecent(int limit = 1000)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, track_id, path, title, artist, genre, started_utc, played_seconds
            FROM history ORDER BY started_utc DESC LIMIT @lim;
            """;
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<HistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HistoryEntry
            {
                Id = r.GetInt64(0),
                TrackId = DbValue.GetLongOrNull(r, 1),
                Path = r.GetString(2),
                Title = DbValue.GetStringOrNull(r, 3),
                Artist = DbValue.GetStringOrNull(r, 4),
                Genre = DbValue.GetStringOrNull(r, 5),
                StartedUtc = DbValue.GetDate(r, 6),
                PlayedSeconds = r.GetInt32(7)
            });
        }
        return list;
    }

    public int Count()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM history;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Genre histogram of what was actually played in a window - powers recommendations.</summary>
    public Dictionary<string, int> GenrePreferenceSince(DateTime sinceUtc)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT COALESCE(genre, 'Unclassified'), COUNT(*) FROM history WHERE started_utc >= @since GROUP BY 1;";
        cmd.Parameters.AddWithValue("@since", DbValue.ToDb(sinceUtc));
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }
}
