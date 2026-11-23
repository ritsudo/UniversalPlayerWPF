using System.IO;
using UniversalPlayer.Classification;
using UniversalPlayer.Classification.Content;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Genres;
using UniversalPlayer.Core.Library;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.App.Services;

/// <summary>
/// Coordinates a full rescan, the genre classification pass and the experimental
/// content analysis pass.
/// </summary>
public sealed class LibraryService
{
    private readonly Database _db;
    private readonly TrackRepository _tracks;
    private readonly ArtistRepository _artists;
    private readonly ScanFolderRepository _scanFolders;
    private readonly ClassificationService _classifier;
    private readonly ContentAnalyzer _content;

    public LibraryService(
        Database db,
        TrackRepository tracks,
        ArtistRepository artists,
        ScanFolderRepository scanFolders,
        ClassificationService classifier,
        ContentAnalyzer content)
    {
        _db = db;
        _tracks = tracks;
        _artists = artists;
        _scanFolders = scanFolders;
        _classifier = classifier;
        _content = content;
    }

    public event Action<string>? Status;
    public event Action<int, int>? Progress;

    public string ContentModelPath => _content.ModelPath;

    public Task<ScanReport> RescanAsync(string root, bool fastScan, CancellationToken ct) =>
        Task.Run(() =>
        {
            var scanner = new LibraryScanner(_db, _tracks, _scanFolders);
            scanner.Progress += (done, total, file) =>
            {
                Status?.Invoke($"Scanning {done}/{total} - {Path.GetFileName(file)}");
                Progress?.Invoke(done, total);
            };
            Status?.Invoke(fastScan ? $"Fast scan of {root} ..." : $"Full scan of {root} ...");
            var report = scanner.Scan(root, fastScan, ct);
            Status?.Invoke(report.Summary);
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

    /// <summary>
    /// The experimental content analysis: describe each recording with tags such as
    /// "piano", "guitar solo", "calm", "vocals" and store them (editable) in the database.
    /// Cancellable, and skips already-analysed tracks when <paramref name="onlyUnanalysed"/> is set.
    /// </summary>
    public Task<int> AnalyzeContentAsync(bool onlyUnanalysed, CancellationToken ct) =>
        Task.Run(() =>
        {
            var all = _tracks.Search(null, null, 200_000);
            var todo = onlyUnanalysed ? all.Where(t => !t.IsAnalysed).ToList() : all;
            if (todo.Count == 0)
            {
                Status?.Invoke("Nothing to analyse - every track is already analysed.");
                return 0;
            }

            if (_content.NeedsCalibration)
            {
                Status?.Invoke("Calibrating the content model on a sample of your collection ...");
                _content.Calibrate(all.Select(t => t.Path).ToList(), 150, ct);
            }

            Status?.Invoke($"Analysing {todo.Count} track(s) with the local content model ...");
            var done = 0;

            foreach (var track in todo)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = _content.Analyze(track.Path, ct);
                    var tags = ContentTagComposer.Compose(result?.Tags, track.Year, track.Genre);
                    _tracks.SetAnalysis(track.Id, tags, result?.Confidence ?? 0);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // undecodable / unsupported: mark as analysed so it is not retried forever
                    _tracks.SetAnalysis(track.Id, null, 0);
                }

                done++;
                Progress?.Invoke(done, todo.Count);
                if (done % 5 == 0 || done == todo.Count)
                    Status?.Invoke($"Analysed {done}/{todo.Count}: {track.DisplayTitle}");
            }

            Status?.Invoke($"Content analysis finished ({done} track(s)).");
            return done;
        }, ct);
}
