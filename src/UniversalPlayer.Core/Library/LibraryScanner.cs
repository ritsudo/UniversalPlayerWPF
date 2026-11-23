using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Library;

/// <summary>
/// Keeps the library in sync with disk.
///
/// A "fast" scan only opens folders whose own last-write time changed (or that are new), so
/// adding one track re-checks one folder instead of the whole library; everything else keeps
/// its signature and is skipped entirely. A file that merely moved to another folder keeps its
/// row - play counts, genre and analysis tags included - and is re-pointed to the new path.
/// </summary>
public sealed class LibraryScanner
{
    private readonly Database _db;
    private readonly TrackRepository _tracks;
    private readonly ScanFolderRepository _folders;

    public LibraryScanner(Database db, TrackRepository tracks, ScanFolderRepository folders)
    {
        _db = db;
        _tracks = tracks;
        _folders = folders;
    }

    public event Action<int, int, string>? Progress;

    public ScanReport Scan(string root, bool fastScan, CancellationToken ct = default)
    {
        var report = new ScanReport { FastScan = fastScan };
        var watch = Stopwatch.StartNew();

        if (!Directory.Exists(root))
        {
            report.Errors.Add($"Music folder not found: {root}");
            return report;
        }

        var now = DateTime.UtcNow;
        var pathIndex = _tracks.LoadPathIndex();
        var parked = fastScan ? _tracks.LoadParked() : new Dictionary<string, Track>();
        var previous = _folders.Load();

        // ---- which folders actually need work? ----
        var dirs = new List<DirScan>();
        foreach (var dir in EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var dirMtime = SafeMtime(dir);
            previous.TryGetValue(dir, out var stored);

            // fast path: nothing was added, removed or renamed inside this folder
            if (fastScan && stored is not null && Ms(stored.DirMtimeUtc) == dirMtime)
            {
                dirs.Add(new DirScan(dir, stored.FileCount, stored.TotalSize, stored.MaxMtimeUtc, stored.NameHash, dirMtime) { Unchanged = true });
                report.FilesSeen += stored.FileCount;
                continue;
            }

            var files = ReadFolder(dir);
            var scan = new DirScan(dir, files.Count, files.Sum(f => f.Size),
                files.Count == 0 ? DateTime.MinValue : files.Max(f => f.Mtime), HashNames(files), dirMtime)
            {
                Files = files
            };            scan.Unchanged = fastScan && stored is not null && scan.MatchesSignature(stored);
            dirs.Add(scan);
            report.FilesSeen += files.Count;
        }

        report.FoldersTotal = dirs.Count;
        var changed = dirs.Where(d => !d.Unchanged).ToList();
        report.FoldersScanned = changed.Count;
        report.FoldersSkipped = dirs.Count - changed.Count;

        var presentDirs = new HashSet<string>(dirs.Select(d => d.Dir), StringComparer.OrdinalIgnoreCase);
        var vanishedDirs = previous.Keys.Where(d => !presentDirs.Contains(d)).ToList();

        var totalFiles = changed.Sum(d => d.Files.Count);
        var examined = 0;

        _db.InTransaction((conn, tx) =>
        {
            Execute(conn, tx, "CREATE TEMP TABLE IF NOT EXISTS _seen_dir(dir TEXT PRIMARY KEY); DELETE FROM _seen_dir;");
            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO _seen_dir(dir) VALUES(@d);";
                var param = insert.Parameters.Add("@d", SqliteType.Text);
                foreach (var dir in dirs)
                {
                    param.Value = dir.Dir;
                    insert.ExecuteNonQuery();
                }
            }

            // everything is "gone" until proven present again (folder level, so unchanged
            // folders cost one statement rather than one per file)
            Execute(conn, tx, "UPDATE tracks SET missing = 1;");
            using (var unmark = conn.CreateCommand())
            {
                unmark.Transaction = tx;
                unmark.CommandText = "UPDATE tracks SET missing = 0, last_seen_utc = @now WHERE dir IN (SELECT dir FROM _seen_dir);";
                unmark.Parameters.AddWithValue("@now", DbValue.ToDb(now));
                unmark.ExecuteNonQuery();
            }

            // ---- pass 1: files that disappeared, and remember them as move candidates ----
            foreach (var dir in changed)
            {
                ct.ThrowIfCancellationRequested();
                var here = new HashSet<string>(dir.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

                foreach (var (id, path, size) in TracksInFolder(conn, tx, dir.Dir))
                {
                    if (here.Contains(path)) continue;

                    _tracks.MarkMissing(conn, tx, id);
                    var track = _tracks.GetById(conn, tx, id);
                    if (track is not null)
                        parked.TryAdd(TrackRepository.ParkKey(track.FileName, size), track);
                }
            }

            // ---- pass 2: new and changed files ----
            foreach (var dir in changed)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var file in dir.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var parkKey = TrackRepository.ParkKey(Path.GetFileNameWithoutExtension(file.Path), file.Size);

                    if (pathIndex.TryGetValue(file.Path, out var known))
                    {
                        if (known.SizeBytes == file.Size && Ms(known.FileModifiedUtc) == file.Mtime)
                            continue; // same content - nothing to do

                        var id = _tracks.Upsert(conn, tx, BuildTrack(file, dir.Dir, root, known.AddedUtc, known.Genre, known.GenreSource, known.ClassifiedUtc));
                        ResetAnalysis(conn, tx, id);   // the file changed, so re-analyse it later
                        report.Updated++;
                    }
                    else if (parked.TryGetValue(parkKey, out var movedTrack))
                    {
                        parked.Remove(parkKey);
                        MoveTrack(movedTrack, file, dir.Dir, root, now);
                        _tracks.UpdateLocation(conn, tx, movedTrack);
                        report.Moved++;
                    }
                    else
                    {
                        _tracks.Upsert(conn, tx, BuildTrack(file, dir.Dir, root, null, null, GenreSource.None, null));
                        report.Added++;
                    }

                    if (++examined % 25 == 0)
                        Progress?.Invoke(examined, totalFiles, file.Path);
                }
            }

            _folders.Apply(conn, tx, dirs.Select(d => d.ToFolder()).ToList(), vanishedDirs);

            using (var count = conn.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 1;";
                report.Missing = Convert.ToInt32(count.ExecuteScalar());
            }

            return 0;
        });

        report.Scanned = report.Added + report.Updated + report.Moved;
        report.Elapsed = watch.Elapsed;
        Progress?.Invoke(examined, totalFiles, string.Empty);
        return report;
    }

    private static Track BuildTrack(FileEntry file, string dir, string root, DateTime? addedUtc, string? genre, GenreSource source, DateTime? classifiedUtc)
    {
        var track = TagReader.Read(file.Path);
        track.Dir = dir;
        track.Folder = RelativeFolder(root, file.Path);
        if (addedUtc.HasValue)
            track.AddedUtc = addedUtc.Value;

        if (string.IsNullOrWhiteSpace(track.Genre) && !string.IsNullOrWhiteSpace(genre))
        {
            track.Genre = genre;
            track.GenreSource = source;
            track.ClassifiedUtc = classifiedUtc;
        }

        return track;
    }

    private static void MoveTrack(Track track, FileEntry file, string dir, string root, DateTime now)
    {
        track.Path = file.Path;
        track.FileName = Path.GetFileNameWithoutExtension(file.Path);
        track.Dir = dir;
        track.Folder = RelativeFolder(root, file.Path);
        track.SizeBytes = file.Size;
        track.FileModifiedUtc = file.Mtime;
        track.LastSeenUtc = now;
        track.Missing = false;
    }

    private static IEnumerable<(long Id, string Path, long Size)> TracksInFolder(SqliteConnection conn, SqliteTransaction tx, string dir)
    {
        var rows = new List<(long, string, long)>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, path, size_bytes FROM tracks WHERE dir = @d AND missing = 0;";
        cmd.Parameters.AddWithValue("@d", dir);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return rows;
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ResetAnalysis(SqliteConnection conn, SqliteTransaction tx, long trackId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE tracks SET analysed_utc = NULL WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", trackId);
        cmd.ExecuteNonQuery();
    }

    private static string RelativeFolder(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        var sep = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return sep > 0 ? rel[..sep] : string.Empty;
    }

    private static List<FileEntry> ReadFolder(string dir)
    {
        var files = new List<FileEntry>();
        try
        {
            foreach (var path in Directory.GetFiles(dir))
            {
                if (!AudioFileTypes.IsAudioFile(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    files.Add(new FileEntry(path, info.Length, Ms(info.LastWriteTimeUtc)));
                }
                catch
                {
                    // unreadable file: skip
                }
            }
        }
        catch
        {
            // unreadable folder: skip
        }
        return files;
    }

    /// <summary>The database keeps timestamps to the millisecond, so compare at that precision.</summary>
    private static DateTime Ms(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, value.Kind);

    private static string HashNames(IReadOnlyCollection<FileEntry> files)
    {
        if (files.Count == 0) return string.Empty;
        var names = files
            .Select(f => (Path.GetFileName(f.Path) ?? string.Empty).ToLowerInvariant())
            .OrderBy(n => n, StringComparer.Ordinal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', names)));
        return Convert.ToHexString(bytes);
    }

    private static DateTime SafeMtime(string dir)
    {
        try { return Ms(Directory.GetLastWriteTimeUtc(dir)); }
        catch { return DateTime.MinValue; }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var stack = new Stack<string>();
        stack.Push(Path.TrimEndingDirectorySeparator(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            yield return dir;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue;
                if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(sub);
            }
        }
    }

    private readonly record struct FileEntry(string Path, long Size, DateTime Mtime);

    private sealed class DirScan
    {
        public DirScan(string dir, int fileCount, long totalSize, DateTime maxMtimeUtc, string nameHash, DateTime dirMtimeUtc)
        {
            Dir = dir;
            FileCount = fileCount;
            TotalSize = totalSize;
            MaxMtimeUtc = maxMtimeUtc;
            NameHash = nameHash;
            DirMtimeUtc = dirMtimeUtc;
        }

        public string Dir { get; }
        public int FileCount { get; }
        public long TotalSize { get; }
        public DateTime MaxMtimeUtc { get; }
        public string NameHash { get; }
        public DateTime DirMtimeUtc { get; }
        public bool Unchanged { get; set; }
        public List<FileEntry> Files { get; set; } = new();

        public bool MatchesSignature(ScanFolder other) =>
            other.FileCount == FileCount &&
            other.TotalSize == TotalSize &&
            Ms(other.MaxMtimeUtc) == MaxMtimeUtc &&
            string.Equals(other.NameHash, NameHash, StringComparison.Ordinal);

        public ScanFolder ToFolder() => new()
        {
            Dir = Dir,
            FileCount = FileCount,
            TotalSize = TotalSize,
            MaxMtimeUtc = MaxMtimeUtc,
            NameHash = NameHash,
            DirMtimeUtc = DirMtimeUtc
        };
    }
}
