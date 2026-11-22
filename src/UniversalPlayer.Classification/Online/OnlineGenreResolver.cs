using System.Collections.Concurrent;
using UniversalPlayer.Core.Genres;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Classification.Online;

/// <summary>
/// Combines the online providers into an ordered lookup: Deezer first (fast, no key),
/// then MusicBrainz. Raw labels are normalised through <see cref="GenreCatalog"/>.
///
/// The track lookup is used when it is confident; if the online track search returns
/// several conflicting labels (common for tribute/compilation hits) the artist's main
/// genre wins instead. Artist results are cached for the session.
/// </summary>
public sealed class OnlineGenreResolver
{
    private readonly DeezerClient _deezer;
    private readonly MusicBrainzClient _musicBrainz;
    private readonly bool _useMusicBrainz;
    private readonly ConcurrentDictionary<string, ClassificationResult> _artistCache = new(StringComparer.OrdinalIgnoreCase);

    public OnlineGenreResolver(bool useMusicBrainz = true)
    {
        _deezer = new DeezerClient();
        _musicBrainz = new MusicBrainzClient();
        _useMusicBrainz = useMusicBrainz;
    }

    /// <summary>Looks a genre up by track first, falling back to the artist's genre.</summary>
    public async Task<ClassificationResult> ResolveTrackAsync(string? artist, string? title, CancellationToken ct)
    {
        string? trackRaw = null;
        var artistMatched = true;
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            var deezer = await _deezer.GetTrackGenreAsync(artist!, title!, ct).ConfigureAwait(false);
            trackRaw = deezer.Genre;
            artistMatched = deezer.ArtistMatched;

            if (string.IsNullOrWhiteSpace(trackRaw) && _useMusicBrainz)
            {
                trackRaw = await _musicBrainz.GetRecordingGenreAsync(artist!, title!, ct).ConfigureAwait(false);
                artistMatched = true;
            }
        }

        var trackGenre = GenreCatalog.Normalize(trackRaw);
        var ambiguous = CountDistinct(trackRaw) > 1 || (trackGenre is not null && !artistMatched);

        // A single, clear label from the track search is good enough - no artist lookup needed.
        if (trackGenre is not null && !ambiguous)
            return new ClassificationResult
            {
                Genre = trackGenre,
                Source = GenreSource.OnlineTrack,
                Confidence = 0.8,
                Detail = $"online track -> {trackRaw}"
            };

        var artistResult = await ResolveArtistAsync(artist, ct).ConfigureAwait(false);

        if (trackGenre is not null && !(ambiguous && !artistResult.IsEmpty))
            return new ClassificationResult
            {
                Genre = trackGenre,
                Source = GenreSource.OnlineTrack,
                Confidence = 0.8,
                Detail = $"online track -> {trackRaw}"
            };

        if (!artistResult.IsEmpty)
            return new ClassificationResult
            {
                Genre = artistResult.Genre,
                Source = GenreSource.OnlineArtist,
                Confidence = 0.7,
                Detail = artistResult.Detail
            };

        return trackGenre is null
            ? ClassificationResult.Empty()
            : new ClassificationResult
            {
                Genre = trackGenre,
                Source = GenreSource.OnlineTrack,
                Confidence = 0.6,
                Detail = $"online track -> {trackRaw}"
            };
    }

    public async Task<ClassificationResult> ResolveArtistAsync(string? artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return ClassificationResult.Empty();

        if (_artistCache.TryGetValue(artist!, out var cached))
            return cached;

        var raw = await _deezer.GetArtistGenreAsync(artist!, ct).ConfigureAwait(false);
        var normalised = GenreCatalog.Normalize(raw);
        if (normalised is not null)
            return Cache(artist!, new ClassificationResult { Genre = normalised, Source = GenreSource.OnlineArtist, Confidence = 0.7, Detail = $"Deezer artist -> {raw}" });

        if (_useMusicBrainz)
        {
            raw = await _musicBrainz.GetArtistGenreAsync(artist!, ct).ConfigureAwait(false);
            normalised = GenreCatalog.Normalize(raw);
            if (normalised is not null)
                return Cache(artist!, new ClassificationResult { Genre = normalised, Source = GenreSource.OnlineArtist, Confidence = 0.6, Detail = $"MusicBrainz artist -> {raw}" });
        }

        return ClassificationResult.Empty();
    }

    private ClassificationResult Cache(string artist, ClassificationResult result)
    {
        _artistCache[artist] = result;
        return result;
    }

    private static int CountDistinct(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? 0
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}
