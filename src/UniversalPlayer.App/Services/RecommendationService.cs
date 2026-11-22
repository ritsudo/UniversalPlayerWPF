using UniversalPlayer.Classification;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Genres;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.App.Services;

/// <summary>
/// "Next song mode": when the queue runs out, pick a track whose genre is close to the
/// one being played, biased by what the listener actually plays and away from recent
/// repeats - the same idea streaming services use for autoplay.
/// </summary>
public sealed class RecommendationService
{
    private readonly TrackRepository _tracks;
    private readonly HistoryRepository _history;

    public RecommendationService(TrackRepository tracks, HistoryRepository history)
    {
        _tracks = tracks;
        _history = history;
    }

    public Track? RecommendNext(Track? current, IReadOnlyCollection<string> recentlyPlayedPaths)
    {
        var histogram = _tracks.GenreHistogram();
        if (histogram.Count == 0)
            return null;

        var preference = _history.GenrePreferenceSince(DateTime.UtcNow.AddDays(30));
        var family = current is null ? "Unknown" : GenreCatalog.Family(current.Genre);

        var candidates = histogram.Keys
            .Where(g => GenreCatalog.Family(g) == family)
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = preference
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .Where(histogram.ContainsKey)
                .Take(5)
                .ToList();
        }

        if (candidates.Count == 0)
            candidates = histogram.Keys.ToList();

        var pool = _tracks.GetByGenres(candidates, current?.Id, 400);
        if (pool.Count == 0)
            pool = _tracks.GetByGenres(histogram.Keys, current?.Id, 400);
        if (pool.Count == 0)
            return null;

        var recent = new HashSet<string>(recentlyPlayedPaths, StringComparer.OrdinalIgnoreCase);
        var fresh = pool.Where(t => !recent.Contains(t.Path)).ToList();
        if (fresh.Count == 0) fresh = pool;

        Track? best = null;
        var bestScore = double.MinValue;
        foreach (var t in fresh)
        {
            var artistBoost = 1.0 + Math.Min(t.PlayCount, 20);
            var genreBoost = 1.0 + preference.GetValueOrDefault(t.Genre ?? "Unclassified");
            var daysSince = t.LastPlayedUtc is null ? 30 : (DateTime.UtcNow - t.LastPlayedUtc.Value).TotalDays;
            var recency = 1.0 / (1.0 + Math.Min(daysSince, 60));
            var score = artistBoost * genreBoost * recency * (0.5 + Random.Shared.NextDouble());

            if (score > bestScore)
            {
                bestScore = score;
                best = t;
            }
        }

        return best;
    }
}
