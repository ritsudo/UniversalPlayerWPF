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
            search_text      TEXT    NOT NULL DEFAULT ''
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
            played_seconds INTEGER NOT NULL DEFAULT 0
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
