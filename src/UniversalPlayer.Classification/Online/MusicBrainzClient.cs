using System.Net;
using System.Text.Json;

namespace UniversalPlayer.Classification.Online;

/// <summary>
/// MusicBrainz fallback. Public and key-free but rate limited to ~1 request/second,
/// so it is only consulted when Deezer returns nothing. Disables itself after repeated
/// failures so an unavailable service never stalls a scan.
/// </summary>
public sealed class MusicBrainzClient
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private static int _consecutiveFailures;
    private static bool _disabled;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri("https://musicbrainz.org/ws/2/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPlayer/1.0 (https://example.local/universal-player)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    public async Task<string?> GetRecordingGenreAsync(string artist, string title, CancellationToken ct)
    {
        var query = $"recording:\"{Quote(title)}\" AND artist:\"{Quote(artist)}\"";
        var search = await GetJsonAsync($"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=3", ct).ConfigureAwait(false);
        var id = FirstString(search, "recordings", "id");
        if (id is null) return null;
        var detail = await GetJsonAsync($"recording/{id}?inc=genres+tags&fmt=json", ct).ConfigureAwait(false);
        return FirstGenreOrTag(detail);
    }

    public async Task<string?> GetArtistGenreAsync(string artist, CancellationToken ct)
    {
        var search = await GetJsonAsync($"artist?query=artist:\"{Uri.EscapeDataString(Quote(artist))}\"&fmt=json&limit=3", ct).ConfigureAwait(false);
        var id = FirstString(search, "artists", "id");
        if (id is null) return null;
        var detail = await GetJsonAsync($"artist/{id}?inc=genres+tags&fmt=json", ct).ConfigureAwait(false);
        return FirstGenreOrTag(detail);
    }

    private static string? FirstGenreOrTag(JsonDocument? doc)
    {
        if (doc is null) return null;
        var root = doc.RootElement;
        if (root.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            foreach (var g in genres.EnumerateArray())
                if (g.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    return name;
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                if (t.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    return name;
        return null;
    }

    private static string? FirstString(JsonDocument? doc, string arrayName, string property)
    {
        if (doc is null) return null;
        if (doc.RootElement.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty(property, out var p))
                    return p.GetString();
        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        if (_disabled) return null;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastRequest = DateTime.UtcNow;

            using var response = await Http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (++_consecutiveFailures >= 3) _disabled = true;
                return null;
            }

            _consecutiveFailures = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            if (++_consecutiveFailures >= 3) _disabled = true;
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Quote(string value) => value.Replace("\"", " ");
}
