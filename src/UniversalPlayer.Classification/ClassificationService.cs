using UniversalPlayer.Classification.Local;
using UniversalPlayer.Classification.Online;
using UniversalPlayer.Core.Genres;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Classification;

public sealed class ClassificationOptions
{
    public bool UseOnline { get; init; } = true;
    public bool UseLocalModel { get; init; } = true;
    public bool UseId3 { get; init; } = true;
}

/// <summary>
/// The classification pipeline requested for the project:
/// 1. existing ID3 tag   -> normalise and keep;
/// 2. online lookup      -> track first, then the artist's main genre;
/// 3. local ML model      -> rough acoustic analysis for anything still unknown.
/// The chosen genre and its provenance (which stage produced it) are returned so the
/// database can cache the result and the UI can show how confident the guess is.
/// </summary>
public sealed class ClassificationService
{
    private readonly OnlineGenreResolver _online;
    private readonly LocalGenreModel _local;

    public ClassificationService(OnlineGenreResolver online, LocalGenreModel local)
    {
        _online = online;
        _local = local;
    }

    public static ClassificationService CreateDefault() =>
        new(new OnlineGenreResolver(), new LocalGenreModel());

    public async Task<ClassificationResult> ClassifyAsync(
        Track track,
        Artist? artistHint,
        ClassificationOptions options,
        CancellationToken ct = default)
    {
        // 1. Trust an explicit ID3 / manual genre.
        if (options.UseId3 && !string.IsNullOrWhiteSpace(track.Genre) && track.GenreSource != GenreSource.LocalModel)
        {
            var known = GenreCatalog.Normalize(track.Genre) ?? track.Genre!;
            return new ClassificationResult
            {
                Genre = known,
                Source = track.GenreSource == GenreSource.None ? GenreSource.Id3 : track.GenreSource,
                Confidence = 1.0,
                Detail = "from file tags"
            };
        }

        // 2. Online lookup.
        if (options.UseOnline)
        {
            var lookupTitle = TitleCleaner.Clean(track.DisplayTitle);
            var online = await _online.ResolveTrackAsync(track.Artist, lookupTitle, ct).ConfigureAwait(false);
            if (!online.IsEmpty)
                return online;
        }

        // 2b. Reuse the cached artist genre before falling back to the local model.
        if (artistHint is not null && !string.IsNullOrWhiteSpace(artistHint.Genre))
            return new ClassificationResult
            {
                Genre = artistHint.Genre,
                Source = artistHint.GenreSource == GenreSource.None ? GenreSource.OnlineArtist : artistHint.GenreSource,
                Confidence = 0.55,
                Detail = "cached artist genre"
            };

        // 3. Local acoustic model.
        if (options.UseLocalModel && File.Exists(track.Path))
        {
            try
            {
                return _local.Predict(track.Path, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // fall through to "unknown"
            }
        }

        return ClassificationResult.Empty("no genre found");
    }

    /// <summary>Online-only artist lookup used to prime the artist cache.</summary>
    public Task<ClassificationResult> ResolveArtistAsync(string artist, CancellationToken ct = default) =>
        _online.ResolveArtistAsync(artist, ct);
}
