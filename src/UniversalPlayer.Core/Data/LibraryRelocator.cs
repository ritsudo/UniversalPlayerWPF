using Microsoft.Data.Sqlite;

namespace UniversalPlayer.Core.Data;

/// <summary>
/// Keeps the absolute paths stored in the database valid after the whole bundle folder
/// (music + player) has been moved somewhere else.
///
/// The root the database was last scanned against is remembered in the <c>meta</c> table.
/// When the player starts in a new location and the old root is gone, every stored path
/// (tracks, their folders, the folder signatures and the play history) is re-pointed to the
/// new root. Without this the library list keeps showing files that no longer exist, a rescan
/// adds them all over again and playing any of them fails.
/// </summary>
public static class LibraryRelocator
{
    private const string RootKey = "library_root";

    /// <summary>Called once at startup, before the library is used.</summary>
    public static void Ensure(Database db, string? currentRoot)
    {
        if (string.IsNullOrWhiteSpace(currentRoot))
            return;

        currentRoot = Normalize(currentRoot);
        var meta = new MetaRepository(db);

        var previous = meta.Get(RootKey);
        previous ??= InferPreviousRoot(db);
        if (previous is not null)
        {
            previous = Normalize(previous);

            // Only treat this as a move of the same library: the recorded root must be gone
            // and the new one must exist. Choosing a different folder while the old one is
            // still there is a normal rescan, not a relocation.
            if (!string.Equals(previous, currentRoot, StringComparison.OrdinalIgnoreCase)
                && !Directory.Exists(previous)
                && Directory.Exists(currentRoot))
            {
                Rebase(db, previous, currentRoot);
            }
        }

        meta.Set(RootKey, currentRoot);
    }

    private static void Rebase(Database db, string oldRoot, string newRoot)
    {
        var oldPrefix = oldRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var newPrefix = newRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        db.InTransaction((conn, tx) =>
        {
            Rewrite(conn, tx, "tracks", "id", "path", oldPrefix, newPrefix);
            Rewrite(conn, tx, "tracks", "id", "dir", oldPrefix, newPrefix);
            Rewrite(conn, tx, "history", "id", "path", oldPrefix, newPrefix);
            Rewrite(conn, tx, "scan_folders", "dir", "dir", oldPrefix, newPrefix);
            return 0;
        });
    }

    private static void Rewrite(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string keyColumn,
        string valueColumn,
        string oldPrefix,
        string newPrefix)
    {
        var updates = new List<(string Key, string Value)>();

        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT {keyColumn}, {valueColumn} FROM {table} WHERE {valueColumn} IS NOT NULL;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                if (TryRebase(value, oldPrefix, newPrefix, out var rebased))
                    updates.Add((key, rebased));
            }
        }

        if (updates.Count == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = string.Equals(keyColumn, valueColumn, StringComparison.OrdinalIgnoreCase)
            ? $"UPDATE {table} SET {valueColumn} = @new WHERE {valueColumn} = @old;"
            : $"UPDATE {table} SET {valueColumn} = @new WHERE {keyColumn} = @key;";
        var pNew = cmd.Parameters.Add("@new", SqliteType.Text);
        var pOld = cmd.Parameters.Add("@old", SqliteType.Text);
        var pKey = cmd.Parameters.Add("@key", SqliteType.Text);

        foreach (var (key, value) in updates)
        {
            pNew.Value = value;
            pOld.Value = key;
            pKey.Value = key;
            cmd.ExecuteNonQuery();
        }
    }

    private static bool TryRebase(string path, string oldPrefix, string newPrefix, out string rebased)
    {
        rebased = path;
        if (oldPrefix.Length == 0 || !path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Length == oldPrefix.Length)
        {
            rebased = newPrefix;
            return true;
        }

        var next = path[oldPrefix.Length];
        if (next != Path.DirectorySeparatorChar && next != Path.AltDirectorySeparatorChar)
            return false;

        rebased = newPrefix + path[oldPrefix.Length..];
        return true;
    }

    /// <summary>
    /// Best-effort recovery for a database created before the root was recorded in the
    /// <c>meta</c> table: the previous root is the common folder the stored tracks live in.
    /// </summary>
    private static string? InferPreviousRoot(Database db)
    {
        var dirs = new List<string>();
        try
        {
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT dir, path FROM tracks LIMIT 4000;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? dir = null;
                if (!reader.IsDBNull(0)) dir = reader.GetString(0);
                else if (!reader.IsDBNull(1)) dir = Path.GetDirectoryName(reader.GetString(1));
                if (!string.IsNullOrWhiteSpace(dir))
                    dirs.Add(Normalize(dir));
            }
        }
        catch
        {
            return null;
        }

        var common = LongestCommonDirectory(dirs);
        if (common is null) return null;

        // Never infer a bare drive root (would rewrite far too much) or the program folder.
        if (string.Equals(common, Path.GetPathRoot(common), StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(common, AppPaths.ExeDirectory, StringComparison.OrdinalIgnoreCase))
            return null;

        return common;
    }

    private static string? LongestCommonDirectory(IReadOnlyList<string> dirs)
    {
        if (dirs.Count == 0) return null;

        var common = dirs[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var dir in dirs)
        {
            var candidate = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (common.Length > 0 && !IsUnder(candidate, common))
                common = ParentOf(common);
            if (common.Length == 0) return null;
        }

        return common;
    }

    private static string ParentOf(string directory)
    {
        try { return Path.GetDirectoryName(directory) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool IsUnder(string path, string directory)
    {
        if (string.Equals(path, directory, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            return false;
        var next = path.Length > directory.Length ? path[directory.Length] : '\0';
        return next is '\\' or '/';
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path.Trim(); }
    }
}
