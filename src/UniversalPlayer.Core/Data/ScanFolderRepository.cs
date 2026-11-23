using Microsoft.Data.Sqlite;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Data;

/// <summary>
/// Remembers a signature per folder so a rescan can skip folders nothing happened in
/// (no file added, removed or renamed), which keeps scans of a large library instant.
/// </summary>
public sealed class ScanFolderRepository
{
    private readonly Database _db;
    public ScanFolderRepository(Database db) => _db = db;

    public Dictionary<string, ScanFolder> Load()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT dir, file_count, total_size, max_mtime, name_hash, dir_mtime FROM scan_folders;";
        var map = new Dictionary<string, ScanFolder>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = new ScanFolder
            {
                Dir = r.GetString(0),
                FileCount = r.GetInt32(1),
                TotalSize = r.GetInt64(2),
                MaxMtimeUtc = DbValue.GetDate(r, 3),
                NameHash = DbValue.GetStringOrNull(r, 4) ?? string.Empty,
                DirMtimeUtc = DbValue.GetDate(r, 5)
            };
        }
        return map;
    }

    /// <summary>Stores the signatures of the folders that exist and drops the ones that are gone.</summary>
    public void Apply(
        SqliteConnection conn,
        SqliteTransaction? tx,
        IReadOnlyCollection<ScanFolder> scanned,
        IReadOnlyCollection<string> vanished)
    {
        if (vanished.Count > 0)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM scan_folders WHERE dir = @d;";
            var param = del.Parameters.Add("@d", SqliteType.Text);
            foreach (var dir in vanished)
            {
                param.Value = dir;
                del.ExecuteNonQuery();
            }
        }

        using var upsert = conn.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = """
            INSERT INTO scan_folders(dir, file_count, total_size, max_mtime, name_hash, dir_mtime)
            VALUES(@d, @c, @s, @m, @h, @dm)
            ON CONFLICT(dir) DO UPDATE SET
                file_count = excluded.file_count,
                total_size = excluded.total_size,
                max_mtime  = excluded.max_mtime,
                name_hash  = excluded.name_hash,
                dir_mtime  = excluded.dir_mtime;
            """;
        var pDir = upsert.Parameters.Add("@d", SqliteType.Text);
        var pCount = upsert.Parameters.Add("@c", SqliteType.Integer);
        var pSize = upsert.Parameters.Add("@s", SqliteType.Integer);
        var pMtime = upsert.Parameters.Add("@m", SqliteType.Text);
        var pHash = upsert.Parameters.Add("@h", SqliteType.Text);
        var pDirMtime = upsert.Parameters.Add("@dm", SqliteType.Text);

        foreach (var folder in scanned)
        {
            pDir.Value = folder.Dir;
            pCount.Value = folder.FileCount;
            pSize.Value = folder.TotalSize;
            pMtime.Value = DbValue.ToDb(folder.MaxMtimeUtc);
            pHash.Value = folder.NameHash;
            pDirMtime.Value = DbValue.ToDb(folder.DirMtimeUtc);
            upsert.ExecuteNonQuery();
        }
    }
}
