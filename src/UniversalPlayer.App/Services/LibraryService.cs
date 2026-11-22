using System.IO;
using UniversalPlayer.Classification;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Library;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.App.Services;

/// <summary>
/// Coordinates a full rescan and the background classification pass that tags
/// newly discovered tracks with a genre.
/// </summary>
public sealed class LibraryService
{
    private readonly Database _db;
    private readonly TrackRepository _tracks;
    private readonly ArtistRepository _artists;
    private readonly ClassificationService _classifier;

    public LibraryService(Database db, TrackRepository tracks, ArtistRepository artists, ClassificationService classifier)
    {
        _db = db;
        _tracks = tracks;
        _artists = artists;
        _classifier = classifier;
    }

    public event Action<string>? Status;
    public event Action<int, int>? Progress;

    public Task<ScanReport> RescanAsync(string root, CancellationToken ct) =>
        Task.Run(() =>
        {
            var scanner = new LibraryScanner(_db, _tracks);
            scanner.Progress += (done, total, file) =>
            {
                Status?.Invoke($"Scanning {done}/{total} - {Path.GetFileName(file)}");
                Progress?.Invoke(done, total);
            };
            Status?.Invoke($"Scanning {root} ...");
            var report = scanner.Scan(root, ct);
            Status?.Invoke($"Scan complete: {report.Added} new, {report.Updated} updated, {report.Missing} missing.");
            return report;
        }, ct);

    /// <summary>Classifies every track that has no genre yet. Returns the number processed.</summary>
    public async Task<int> ClassifyPendingAsync(ClassificationOptions options, CancellationToken ct)
    {
        var done = 0;
        var total = _tracks.CountUnclassified();
        if (total == 0)
        {
            Status?.Invoke("All tracks are already classified.");
            return 0;
        }

        Status?.Invoke($"Classifying {total} track(s) ...");
        var artistCache = new Dictionary<string, Artist?>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            var batch = _tracks.GetUnclassified(100);
            if (batch.Count == 0) break;

            foreach (var track in batch)
            {
                ct.ThrowIfCancellationRequested();

                Artist? hint = null;
                if (!string.IsNullOrWhiteSpace(track.Artist))
                {
                    if (!artistCache.TryGetValue(track.Artist!, out hint))
                    {
                        hint = _artists.Get(track.Artist!);
                        artistCache[track.Artist!] = hint;
                    }
                }

                ClassificationResult result;
                try
                {
                    var classifyTask = _classifier.ClassifyAsync(track, hint, options, ct);
                    var finished = await Task.WhenAny(classifyTask, Task.Delay(TimeSpan.FromSeconds(25), ct)).ConfigureAwait(false);
                    result = finished == classifyTask
                        ? await classifyTask.ConfigureAwait(false)
                        : ClassificationResult.Empty("timed out");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    result = ClassificationResult.Empty("classification error");
                }

                _tracks.SetClassification(track.Id, result.Genre, result.Source);

                if (!string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(result.Genre))
                {
                    var existing = hint;
                    // Only artist-level lookups become the cached "main genre"; track-level and
                    // model guesses are stored with their own provenance when nothing is known yet.
                    if (result.Source is GenreSource.OnlineArtist)
                    {
                        _artists.Upsert(track.Artist!, result.Genre, GenreSource.OnlineArtist);
                        artistCache[track.Artist!] = new Artist { Name = track.Artist!, Genre = result.Genre, GenreSource = GenreSource.OnlineArtist };
                    }
                    else if (existing is null && result.Source is GenreSource.LocalModel or GenreSource.OnlineTrack)
                    {
                        _artists.Upsert(track.Artist!, result.Genre, result.Source);
                        artistCache[track.Artist!] = new Artist { Name = track.Artist!, Genre = result.Genre, GenreSource = result.Source };
                    }
                }

                done++;
                Progress?.Invoke(done, total);
                Status?.Invoke($"Classified {done}/{total}: {track.DisplayTitle} [{result.Genre ?? "unknown"}]");
            }
        }

        Status?.Invoke($"Classification finished ({done} track(s)).");
        return done;
    }
}
