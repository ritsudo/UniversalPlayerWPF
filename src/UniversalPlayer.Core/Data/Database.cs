using Microsoft.Data.Sqlite;

namespace UniversalPlayer.Core.Data;

/// <summary>Owns the SQLite file and creates the schema.</summary>
public sealed class Database
{
    public string FilePath { get; }
    private readonly string _connectionString;

    public Database(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA foreign_keys=ON;" +
            "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Runs <paramref name="work"/> inside a single connection and transaction.</summary>
    public T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var result = work(conn, tx);
        tx.Commit();
        return result;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        EnsureColumns(conn);
    }

    /// <summary>Adds columns/tables introduced after the first release, so an existing database keeps working.</summary>
    private static void EnsureColumns(SqliteConnection conn)
    {
        var additions = new (string Table, string Name, string Ddl)[]
        {
            ("tracks", "content_tags", "ALTER TABLE tracks ADD COLUMN content_tags TEXT;"),
            ("tracks", "analysed_utc", "ALTER TABLE tracks ADD COLUMN analysed_utc TEXT;"),
            ("tracks", "analysis_conf", "ALTER TABLE tracks ADD COLUMN analysis_conf REAL;"),
            ("tracks", "dir", "ALTER TABLE tracks ADD COLUMN dir TEXT;"),
            ("history", "kind", "ALTER TABLE history ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;")
        };

        var existing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in additions.Select(a => a.Table).Distinct())
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = conn.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info({table});";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
            }
            existing[table] = columns;
        }

        var addedDir = false;
        foreach (var (table, name, ddl) in additions)
        {
            if (existing[table].Contains(name)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
            if (table == "tracks" && name == "dir") addedDir = true;
        }

        if (addedDir)
            BackfillDir(conn);
    }

    /// <summary>Fills the new "dir" column from each track's path (one-off migration).</summary>
    private static void BackfillDir(SqliteConnection conn)
    {
        var rows = new List<(long Id, string Path)>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT id, path FROM tracks;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        using var tx = conn.BeginTransaction();
        foreach (var (id, path) in rows)
        {
            string dir;
            try { dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty; }
            catch { dir = string.Empty; }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE tracks SET dir = @d WHERE id = @id;";
            cmd.Parameters.AddWithValue("@d", dir);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS tracks (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            path             TEXT    NOT NULL UNIQUE,
            file_name        TEXT    NOT NULL,
            folder           TEXT    NOT NULL DEFAULT '',
            title            TEXT,
            artist           TEXT,
            album            TEXT,
            genre            TEXT,
            genre_source     TEXT    NOT NULL DEFAULT 'None',
            year             INTEGER,
            track_number     INTEGER,
            duration_ms      INTEGER NOT NULL DEFAULT 0,
            bitrate          INTEGER,
            sample_rate      INTEGER,
            channels         INTEGER,
            size_bytes       INTEGER NOT NULL DEFAULT 0,
            file_modified_utc TEXT   NOT NULL,
            added_utc        TEXT    NOT NULL,
            last_seen_utc    TEXT    NOT NULL,
            classified_utc   TEXT,
            play_count       INTEGER NOT NULL DEFAULT 0,
            last_played_utc  TEXT,
            missing          INTEGER NOT NULL DEFAULT 0,
            search_text      TEXT    NOT NULL DEFAULT '',
            content_tags     TEXT,
            analysed_utc     TEXT,
            analysis_conf    REAL,
            dir              TEXT
        );

        CREATE TABLE IF NOT EXISTS scan_folders (
            dir        TEXT PRIMARY KEY,
            file_count INTEGER NOT NULL DEFAULT 0,
            total_size INTEGER NOT NULL DEFAULT 0,
            max_mtime  TEXT,
            name_hash  TEXT,
            dir_mtime  TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_tracks_artist  ON tracks(artist);
        CREATE INDEX IF NOT EXISTS ix_tracks_genre   ON tracks(genre);
        CREATE INDEX IF NOT EXISTS ix_tracks_folder  ON tracks(folder);
        CREATE INDEX IF NOT EXISTS ix_tracks_missing ON tracks(missing);

        CREATE TABLE IF NOT EXISTS artists (
            name          TEXT PRIMARY KEY,
            genre         TEXT,
            genre_source  TEXT NOT NULL DEFAULT 'None',
            updated_utc   TEXT
        );

        CREATE TABLE IF NOT EXISTS history (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            track_id       INTEGER,
            path           TEXT NOT NULL,
            title          TEXT,
            artist         TEXT,
            genre          TEXT,
            started_utc    TEXT NOT NULL,
            played_seconds INTEGER NOT NULL DEFAULT 0,
            kind           INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_history_started ON history(started_utc DESC);

        CREATE TABLE IF NOT EXISTS playlists (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT NOT NULL UNIQUE,
            created_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS playlist_items (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            playlist_id INTEGER NOT NULL,
            track_id    INTEGER NOT NULL,
            position    INTEGER NOT NULL,
            FOREIGN KEY(playlist_id) REFERENCES playlists(id) ON DELETE CASCADE,
            FOREIGN KEY(track_id)    REFERENCES tracks(id)    ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_playlist_items ON playlist_items(playlist_id, position);

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """;
}
