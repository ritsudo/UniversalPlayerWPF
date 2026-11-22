using Microsoft.Data.Sqlite;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Data;

public sealed class TrackRepository
{
    private readonly Database _db;
    public TrackRepository(Database db) => _db = db;

    private const string Columns =
        "id, path, file_name, folder, title, artist, album, genre, genre_source, year, track_number, " +
        "duration_ms, bitrate, sample_rate, channels, size_bytes, file_modified_utc, added_utc, last_seen_utc, " +
        "classified_utc, play_count, last_played_utc, missing, content_tags, analysed_utc, analysis_conf";

    private static Track Map(SqliteDataReader r) => new()
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
        Missing = r.GetInt32(22) != 0,
        ContentTags = DbValue.GetStringOrNull(r, 23),
        AnalysedUtc = DbValue.GetNullableDate(r, 24),
        AnalysisConfidence = r.IsDBNull(25) ? null : r.GetDouble(25)
    };

    private const string UpsertSql = """
        INSERT INTO tracks
            (path, file_name, folder, title, artist, album, genre, genre_source, year, track_number,
             duration_ms, bitrate, sample_rate, channels, size_bytes, file_modified_utc, added_utc,
             last_seen_utc, classified_utc, missing, search_text)
        VALUES
            (@path, @file_name, @folder, @title, @artist, @album, @genre, @genre_source, @year, @track_number,
             @duration_ms, @bitrate, @sample_rate, @channels, @size_bytes, @file_modified_utc, @added_utc,
             @last_seen_utc, @classified_utc, 0, @search_text)
        ON CONFLICT(path) DO UPDATE SET
            file_name        = excluded.file_name,
            folder           = excluded.folder,
            title            = COALESCE(excluded.title, tracks.title),
            artist           = COALESCE(excluded.artist, tracks.artist),
            album            = COALESCE(excluded.album, tracks.album),
            genre            = CASE WHEN excluded.genre IS NULL OR excluded.genre = '' THEN tracks.genre ELSE excluded.genre END,
            genre_source     = CASE WHEN excluded.genre IS NULL OR excluded.genre = '' THEN tracks.genre_source ELSE excluded.genre_source END,
            classified_utc   = CASE WHEN excluded.genre IS NULL OR excluded.genre = '' THEN tracks.classified_utc ELSE excluded.classified_utc END,
            year             = COALESCE(excluded.year, tracks.year),
            track_number     = COALESCE(excluded.track_number, tracks.track_number),
            duration_ms      = excluded.duration_ms,
            bitrate          = COALESCE(excluded.bitrate, tracks.bitrate),
            sample_rate      = COALESCE(excluded.sample_rate, tracks.sample_rate),
            channels         = COALESCE(excluded.channels, tracks.channels),
            size_bytes       = excluded.size_bytes,
            file_modified_utc= excluded.file_modified_utc,
            last_seen_utc    = excluded.last_seen_utc,
            missing          = 0,
            search_text      = excluded.search_text
        RETURNING id;
        """;

    /// <summary>Inserts or refreshes a track. Returns the row id.</summary>
    public long Upsert(Track t) { using var c = _db.Open(); return Upsert(c, null, t); }

    public long Upsert(SqliteConnection conn, SqliteTransaction? tx, Track t)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpsertSql;
        var searchText = t.BuildSearchText();
        cmd.Parameters.AddWithValue("@path", t.Path);
        cmd.Parameters.AddWithValue("@file_name", t.FileName);
        cmd.Parameters.AddWithValue("@folder", t.Folder);
        cmd.Parameters.AddWithValue("@title", (object?)Nullify(t.Title) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artist", (object?)Nullify(t.Artist) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@album", (object?)Nullify(t.Album) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genre", (object?)Nullify(t.Genre) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@genre_source", t.GenreSource.ToString());
        cmd.Parameters.AddWithValue("@year", (object?)t.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@track_number", (object?)t.TrackNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@duration_ms", t.DurationMs);
        cmd.Parameters.AddWithValue("@bitrate", (object?)t.Bitrate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sample_rate", (object?)t.SampleRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@channels", (object?)t.Channels ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size_bytes", t.SizeBytes);
        cmd.Parameters.AddWithValue("@file_modified_utc", DbValue.ToDb(t.FileModifiedUtc));
        cmd.Parameters.AddWithValue("@added_utc", DbValue.ToDb(t.AddedUtc));
        cmd.Parameters.AddWithValue("@last_seen_utc", DbValue.ToDb(t.LastSeenUtc));
        cmd.Parameters.AddWithValue("@classified_utc", DbValue.ToDb(t.ClassifiedUtc));
        cmd.Parameters.AddWithValue("@search_text", searchText);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Track? GetByPath(string path)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE path = @p LIMIT 1;";
        cmd.Parameters.AddWithValue("@p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public Track? GetById(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public List<Track> Search(string? query, string? genre = null, int limit = 2000)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        var where = "missing = 0";
        if (!string.IsNullOrWhiteSpace(query))
        {
            where += " AND search_text LIKE @q";
            cmd.Parameters.AddWithValue("@q", "%" + query.Trim().ToLowerInvariant() + "%");
        }
        if (!string.IsNullOrWhiteSpace(genre))
        {
            where += " AND genre = @g";
            cmd.Parameters.AddWithValue("@g", genre);
        }
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE {where} ORDER BY artist, album, track_number, title LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public List<Track> GetAll(int limit = 100000) => Search(null, null, limit);

    public List<Track> GetByGenre(string genre, long? excludeId, int limit)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE missing = 0 AND genre = @g AND (@ex IS NULL OR id <> @ex) ORDER BY RANDOM() LIMIT @lim;";
        cmd.Parameters.AddWithValue("@g", genre);
        cmd.Parameters.AddWithValue("@ex", (object?)excludeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public List<Track> GetUnclassified(int limit)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE missing = 0 AND classified_utc IS NULL ORDER BY id LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>Random sample from any of the supplied genres - used by the recommender.</summary>
    public List<Track> GetByGenres(IEnumerable<string> genres, long? excludeId, int limit)
    {
        var list = genres.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return new List<Track>();

        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            names.Add("@g" + i);
            cmd.Parameters.AddWithValue("@g" + i, list[i]);
        }
        cmd.CommandText =
            $"SELECT {Columns} FROM tracks WHERE missing = 0 AND genre IN ({string.Join(",", names)}) " +
            "AND (@ex IS NULL OR id <> @ex) ORDER BY RANDOM() LIMIT @lim;";
        cmd.Parameters.AddWithValue("@ex", (object?)excludeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lim", limit);
        var tracks = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) tracks.Add(Map(r));
        return tracks;
    }

    public void SetClassification(long id, string? genre, GenreSource source)
    {
        using var c = _db.Open();
        SetClassification(c, null, id, genre, source);
    }

    public void SetClassification(SqliteConnection conn, SqliteTransaction? tx, long id, string? genre, GenreSource source)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE tracks SET genre = @g, genre_source = @s, classified_utc = @c WHERE id = @id;";
        cmd.Parameters.AddWithValue("@g", (object?)Nullify(genre) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", source.ToString());
        cmd.Parameters.AddWithValue("@c", DbValue.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void IncrementPlay(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET play_count = play_count + 1, last_played_utc = @t WHERE id = @id;";
        cmd.Parameters.AddWithValue("@t", DbValue.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 0;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountUnclassified()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 0 AND classified_utc IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Dictionary<string, int> GenreHistogram()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(genre, 'Unclassified'), COUNT(*) FROM tracks WHERE missing = 0 GROUP BY 1 ORDER BY 2 DESC;";
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = r.GetInt32(1);
        return result;
    }

    public List<string> DistinctArtistsWithoutGenre()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT artist FROM tracks WHERE missing = 0 AND artist IS NOT NULL AND artist <> '' " +
            "AND classified_utc IS NULL;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void MarkAllMissing()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET missing = 1;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Clears genres that were only guessed by the local acoustic model, so a later online
    /// pass can try to do better. Returns the number of rows reset.
    /// </summary>
    public int ResetLocalModelClassifications()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE tracks SET genre = NULL, genre_source = 'None', classified_utc = NULL WHERE genre_source = 'LocalModel';" +
            "UPDATE artists SET genre = NULL, genre_source = 'None' WHERE genre_source = 'LocalModel';";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Stores the content-analysis result (and marks the track as analysed).</summary>
    public void SetAnalysis(long id, string? tags, double confidence)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE tracks SET content_tags = @t, analysis_conf = @c, analysed_utc = @u WHERE id = @id;";
        cmd.Parameters.AddWithValue("@t", (object?)Nullify(tags) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", confidence);
        cmd.Parameters.AddWithValue("@u", DbValue.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Manual edit of the content tags (keeps the analysed marker).</summary>
    public void UpdateContentTags(long id, string? tags)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET content_tags = @t WHERE id = @id;";
        cmd.Parameters.AddWithValue("@t", (object?)Nullify(tags) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Manual edit of the genre.</summary>
    public void SetGenreManual(long id, string? genre)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET genre = @g, genre_source = @s, classified_utc = @u WHERE id = @id;";
        cmd.Parameters.AddWithValue("@g", (object?)Nullify(genre) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", GenreSource.Manual.ToString());
        cmd.Parameters.AddWithValue("@u", DbValue.ToDb(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<Track> GetUnanalysed(int limit)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tracks WHERE missing = 0 AND analysed_utc IS NULL ORDER BY id LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public int CountUnanalysed()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 0 AND analysed_utc IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountAnalysed()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 0 AND analysed_utc IS NOT NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Tag histogram across the library (tag -> number of tracks).</summary>
    public Dictionary<string, int> TagHistogram()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT content_tags FROM tracks WHERE missing = 0 AND content_tags IS NOT NULL;";
        var histogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            foreach (var tag in r.GetString(0).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                histogram[tag] = histogram.GetValueOrDefault(tag) + 1;
        }
        return histogram;
    }

    /// <summary>
    /// Scans every stored text field for text saved with the wrong code page (Cyrillic
    /// tags shown as "Êîðîëü è Øóò") and repairs it. Returns the number of changed tracks.
    /// </summary>
    public int RepairTextFields()
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();

        var updates = new List<(long Id, string? Title, string? Artist, string? Album, string? Genre, string FileName, string SearchText)>();

        using (var read = c.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT id, title, artist, album, genre, file_name, folder FROM tracks;";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var title = DbValue.GetStringOrNull(r, 1);
                var artist = DbValue.GetStringOrNull(r, 2);
                var album = DbValue.GetStringOrNull(r, 3);
                var genre = DbValue.GetStringOrNull(r, 4);
                var fileName = r.GetString(5);
                var folder = r.GetString(6);

                var fixedTitle = Text.TextRepair.Fix(title);
                var fixedArtist = Text.TextRepair.Fix(artist);
                var fixedAlbum = Text.TextRepair.Fix(album);
                var fixedGenre = Text.TextRepair.Fix(genre);
                var fixedFileName = Text.TextRepair.Fix(fileName);

                if (fixedTitle is null && fixedArtist is null && fixedAlbum is null &&
                    fixedGenre is null && fixedFileName is null)
                    continue;

                var newTitle = fixedTitle ?? title;
                var newArtist = fixedArtist ?? artist;
                var newAlbum = fixedAlbum ?? album;
                var newGenre = fixedGenre ?? genre;
                var newFileName = fixedFileName ?? fileName;

                var search = string.Join('\n', new[] { newTitle, newArtist, newAlbum, newGenre, newFileName, folder }
                    .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();

                updates.Add((id, newTitle, newArtist, newAlbum, newGenre, newFileName, search));
            }
        }

        foreach (var u in updates)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE tracks SET title = @t, artist = @a, album = @al, genre = @g, file_name = @f, search_text = @s WHERE id = @id;";
            cmd.Parameters.AddWithValue("@t", (object?)u.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@a", (object?)u.Artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@al", (object?)u.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@g", (object?)u.Genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", u.FileName);
            cmd.Parameters.AddWithValue("@s", u.SearchText);
            cmd.Parameters.AddWithValue("@id", u.Id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return updates.Count;
    }
}
