using System.Diagnostics;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Library;

/// <summary>
/// Walks the music root, refreshes changed/new files and flags disappeared ones as missing.
/// Files whose size and timestamp are unchanged are skipped entirely, so rescans stay fast.
/// </summary>
public sealed class LibraryScanner
{
    private readonly Database _db;
    private readonly TrackRepository _tracks;

    public LibraryScanner(Database db, TrackRepository tracks)
    {
        _db = db;
        _tracks = tracks;
    }

    public event Action<int, int, string>? Progress;

    public ScanReport Scan(string root, CancellationToken ct = default)
    {
        var report = new ScanReport();
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(root))
        {
            report.Errors.Add($"Music folder not found: {root}");
            return report;
        }

        var existing = _tracks.Search(null, null, 1_000_000)
            .ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();
        foreach (var f in EnumerateAudio(root))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(f);
        }

        var now = DateTime.UtcNow;
        var changed = new List<(string Path, bool IsNew)>();
        foreach (var f in files)
        {
            report.Scanned++;
            if (existing.TryGetValue(f, out var known))
            {
                var info = new FileInfo(f);
                if (info.Length == known.SizeBytes && info.LastWriteTimeUtc == known.FileModifiedUtc)
                    continue; // unchanged - nothing to read
                changed.Add((f, false));
            }
            else
            {
                changed.Add((f, true));
            }
        }

        _db.InTransaction((conn, tx) =>
        {
            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = "UPDATE tracks SET missing = 1;";
                mark.ExecuteNonQuery();
            }

            using (var seen = conn.CreateCommand())
            {
                seen.Transaction = tx;
                seen.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _seen(path TEXT PRIMARY KEY); DELETE FROM _seen;";
                seen.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO _seen(path) VALUES(@p);";
                var param = insert.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Text);
                foreach (var f in files)
                {
                    param.Value = f;
                    insert.ExecuteNonQuery();
                }
            }

            using (var unmark = conn.CreateCommand())
            {
                unmark.Transaction = tx;
                unmark.CommandText =
                    "UPDATE tracks SET missing = 0, last_seen_utc = @now WHERE path IN (SELECT path FROM _seen);";
                unmark.Parameters.AddWithValue("@now", DbValue.ToDb(now));
                unmark.ExecuteNonQuery();
            }

            using (var gone = conn.CreateCommand())
            {
                gone.Transaction = tx;
                gone.CommandText = "SELECT COUNT(*) FROM tracks WHERE missing = 1;";
                report.Missing = Convert.ToInt32(gone.ExecuteScalar());
            }

            var index = 0;
            foreach (var (path, isNew) in changed)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var track = TagReader.Read(path);
                    track.Folder = RelativeFolder(root, path);
                    if (!isNew && existing.TryGetValue(path, out var old))
                    {
                        track.AddedUtc = old.AddedUtc;
                        // keep an existing classification if the new tags do not carry a genre
                        if (string.IsNullOrWhiteSpace(track.Genre))
                        {
                            track.Genre = old.Genre;
                            track.GenreSource = old.GenreSource;
                            track.ClassifiedUtc = old.ClassifiedUtc;
                        }
                    }
                    _tracks.Upsert(conn, tx, track);
                    if (isNew) report.Added++; else report.Updated++;
                }
                catch (Exception ex)
                {
                    report.Failed++;
                    if (report.Errors.Count < 25)
                        report.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }

                if (++index % 40 == 0)
                    Progress?.Invoke(index, changed.Count, path);
            }

            return 0;
        });

        report.Elapsed = sw.Elapsed;
        Progress?.Invoke(changed.Count, changed.Count, string.Empty);
        return report;
    }

    private static string RelativeFolder(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        var sep = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return sep > 0 ? rel[..sep] : string.Empty;
    }

    private static IEnumerable<string> EnumerateAudio(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
                if (AudioFileTypes.IsAudioFile(f))
                    yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(d);
            }
        }
    }
}
