using Microsoft.Data.Sqlite;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Data;

public sealed class PlaylistRepository
{
    private readonly Database _db;
    public PlaylistRepository(Database db) => _db = db;

    public Playlist Create(string name)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists(name, created_utc) VALUES(@n, @t) ON CONFLICT(name) DO NOTHING RETURNING id;";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@t", DbValue.ToDb(DateTime.UtcNow));
        var id = cmd.ExecuteScalar();
        if (id is null)
        {
            using var get = c.CreateCommand();
            get.CommandText = "SELECT id FROM playlists WHERE name = @n;";
            get.Parameters.AddWithValue("@n", name);
            id = get.ExecuteScalar();
        }
        return new Playlist { Id = Convert.ToInt64(id), Name = name, CreatedUtc = DateTime.UtcNow };
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM playlists WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void Rename(long id, string name)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = @n WHERE id = @id;";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<Playlist> GetAll()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.created_utc, COUNT(i.id)
            FROM playlists p LEFT JOIN playlist_items i ON i.playlist_id = p.id
            GROUP BY p.id ORDER BY p.name;
            """;
        var list = new List<Playlist>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Playlist
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                CreatedUtc = DbValue.GetDate(r, 2),
                ItemCount = r.GetInt32(3)
            });
        }
        return list;
    }

    public void AddTrack(long playlistId, long trackId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playlist_items(playlist_id, track_id, position)
            VALUES(@p, @t, COALESCE((SELECT MAX(position) + 1 FROM playlist_items WHERE playlist_id = @p), 0));
            """;
        cmd.Parameters.AddWithValue("@p", playlistId);
        cmd.Parameters.AddWithValue("@t", trackId);
        cmd.ExecuteNonQuery();
    }

    public void RemoveItem(long itemId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.ExecuteNonQuery();
    }

    public List<Track> GetTracks(long playlistId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.path, t.file_name, t.folder, t.title, t.artist, t.album, t.genre, t.genre_source,
                   t.year, t.track_number, t.duration_ms, t.bitrate, t.sample_rate, t.channels, t.size_bytes,
                   t.file_modified_utc, t.added_utc, t.last_seen_utc, t.classified_utc, t.play_count,
                   t.last_played_utc, t.missing
            FROM playlist_items i JOIN tracks t ON t.id = i.track_id
            WHERE i.playlist_id = @p
            ORDER BY i.position;
            """;
        cmd.Parameters.AddWithValue("@p", playlistId);
        var list = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Track
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                FileName = r.GetString(2),
                Folder = r.GetString(3),
                Title = DbValue.GetStringOrNull(r, 4) ?? string.Empty,
                Artist = DbValue.GetStringOrNull(r, 5),
                Album = DbValue.GetStringOrNull(r, 6),
                Genre = DbValue.GetStringOrNull(r, 7),
                GenreSource = Enum.TryParse<GenreSource>(DbValue.GetStringOrNull(r, 8), out var gs) ? gs : GenreSource.None,
                Year = DbValue.GetIntOrNull(r, 9),
                TrackNumber = DbValue.GetIntOrNull(r, 10),
                DurationMs = r.GetInt64(11),
                Bitrate = DbValue.GetIntOrNull(r, 12),
                SampleRate = DbValue.GetIntOrNull(r, 13),
                Channels = DbValue.GetIntOrNull(r, 14),
                SizeBytes = r.GetInt64(15),
                FileModifiedUtc = DbValue.GetDate(r, 16),
                AddedUtc = DbValue.GetDate(r, 17),
                LastSeenUtc = DbValue.GetDate(r, 18),
                ClassifiedUtc = DbValue.GetNullableDate(r, 19),
                PlayCount = r.GetInt32(20),
                LastPlayedUtc = DbValue.GetNullableDate(r, 21),
                Missing = r.GetInt32(22) != 0
            });
        }
        return list;
    }
}
