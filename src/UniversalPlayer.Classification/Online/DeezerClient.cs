using System.Net;
using System.Text.Json;

namespace UniversalPlayer.Classification.Online;

/// <summary>
/// Thin client for the public Deezer API (no key required). Deezer exposes genre
/// information on albums, and artist genres are derived from their top albums.
/// All awaits use ConfigureAwait(false) so the calls never depend on a UI message pump.
/// </summary>
public sealed class DeezerClient
{
    private static readonly HttpClient Http = CreateClient();
    private static int _consecutiveFailures;
    private static bool _disabled;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri("https://api.deezer.com/"),
            Timeout = TimeSpan.FromSeconds(8)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPlayer/1.0");
        return http;
    }

    /// <summary>Raw genre label(s) for a track, plus whether the hit really was that artist
    /// (a false flag means the top results were probably covers/tributes).</summary>
    public readonly record struct TrackGenre(string? Genre, bool ArtistMatched);

    public async Task<TrackGenre> GetTrackGenreAsync(string artist, string title, CancellationToken ct)
    {
        // Deezer's advanced syntax is brittle (quotes/slashes); a plain free-text query
        // works reliably and we simply prefer a result whose artist matches.
        var query = $"{artist} {title}";
        var doc = await GetJsonAsync($"search?q={Uri.EscapeDataString(query)}&limit=8", ct).ConfigureAwait(false);
        var (albumIds, matched) = ExtractAlbumIds(doc, artist, 3);
        if (albumIds.Count == 0)
            return new TrackGenre(null, matched);

        var genre = await FirstGenreFromAlbumsAsync(albumIds, ct).ConfigureAwait(false);
        return new TrackGenre(genre, matched);
    }

    public async Task<string?> GetArtistGenreAsync(string artist, CancellationToken ct)
    {
        var doc = await GetJsonAsync($"search/artist?q={Uri.EscapeDataString(artist)}&limit=3", ct).ConfigureAwait(false);
        if (doc is not null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in data.EnumerateArray().Take(2))
            {
                if (!a.TryGetProperty("id", out var idEl)) continue;
                var albumIds = await GetArtistAlbumIdsAsync(idEl.GetInt64(), ct).ConfigureAwait(false);
                var genre = await FirstGenreFromAlbumsAsync(albumIds, ct).ConfigureAwait(false);
                if (genre is not null) return genre;
            }
        }
        return null;
    }

    private async Task<List<long>> GetArtistAlbumIdsAsync(long artistId, CancellationToken ct)
    {
        var ids = new List<long>();
        var doc = await GetJsonAsync($"artist/{artistId}/albums?limit=10", ct).ConfigureAwait(false);
        if (doc is not null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var alb in data.EnumerateArray())
                if (alb.TryGetProperty("id", out var idEl))
                    ids.Add(idEl.GetInt64());
        return ids;
    }

    private async Task<string?> FirstGenreFromAlbumsAsync(IEnumerable<long> albumIds, CancellationToken ct)
    {
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var albumId in albumIds.Take(3))
        {
            var doc = await GetJsonAsync($"album/{albumId}", ct).ConfigureAwait(false);
            var genre = ExtractAlbumGenre(doc);
            if (genre is not null)
                tally[genre] = tally.GetValueOrDefault(genre) + 1;

            // stop early: two labels means "ambiguous", two of the same means "settled"
            var distinct = tally.Count;
            var best = tally.Count == 0 ? 0 : tally.Values.Max();
            if (distinct >= 2 || best >= 2) break;
        }

        var ordered = tally.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        // Several albums may carry different (often localised) labels; returning them all
        // lets the normaliser pick the first one it recognises.
        return ordered.Count == 0 ? null : string.Join(";", ordered);
    }

    private static (List<long> Ids, bool ArtistMatched) ExtractAlbumIds(JsonDocument? doc, string artist, int max)
    {
        var ordered = new List<long>();
        var fallback = new List<long>();
        if (doc is not null && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in data.EnumerateArray())
            {
                if (!t.TryGetProperty("album", out var album) || !album.TryGetProperty("id", out var idEl))
                    continue;
                var id = idEl.GetInt64();

                var artistMatch = false;
                if (t.TryGetProperty("artist", out var a) && a.TryGetProperty("name", out var nameEl))
                {
                    var name = nameEl.GetString() ?? string.Empty;
                    artistMatch = name.Contains(artist, StringComparison.OrdinalIgnoreCase)
                                  || artist.Contains(name, StringComparison.OrdinalIgnoreCase);
                }

                var bucket = artistMatch ? ordered : fallback;
                if (!bucket.Contains(id)) bucket.Add(id);
            }
        }

        var ids = ordered.Concat(fallback).Distinct().Take(max).ToList();
        return (ids, ordered.Count > 0);
    }

    private static string? ExtractAlbumGenre(JsonDocument? doc)
    {
        if (doc is null) return null;
        var root = doc.RootElement;
        if (root.TryGetProperty("genres", out var genres))
        {
            // newer API: genres.data[].name  |  legacy: genres is a plain array
            var arr = genres.ValueKind == JsonValueKind.Object && genres.TryGetProperty("data", out var inner)
                ? inner
                : genres;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var g in arr.EnumerateArray())
                    if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("name", out var nameEl))
                        return nameEl.GetString();
        }
        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        if (_disabled) return null;
        try
        {
            using var response = await Http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (++_consecutiveFailures >= 3) _disabled = true;
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            // Deezer answers HTTP 200 with {"error":{...}} for quota/limits.
            if (doc.RootElement.TryGetProperty("error", out _))
            {
                if (++_consecutiveFailures >= 3) _disabled = true;
                doc.Dispose();
                return null;
            }

            _consecutiveFailures = 0;
            return doc;
        }
        catch
        {
            if (++_consecutiveFailures >= 3) _disabled = true;
            return null;
        }
    }
}
