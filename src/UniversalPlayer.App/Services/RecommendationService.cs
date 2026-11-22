using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Genres;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.App.Services;

/// <summary>
/// "Next song" picks the most similar track still available. Similarity is driven by the
/// content-analysis tags (piano -&gt; piano, calm -&gt; calm, ...), then by genre and genre
/// family; the strongest-tagged tracks form thematic runs as the session goes on.
/// Nothing is repeated within a session, and when the current "theme" is exhausted the
/// picker drifts to another genre instead of stopping.
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

    public sealed record Recommendation(Track Track, string Reason);

    public Recommendation? RecommendNext(Track? current, IReadOnlyCollection<string> sessionPlayed)
    {
        var all = _tracks.Search(null, null, 200_000);
        if (all.Count == 0) return null;

        var played = new HashSet<string>(sessionPlayed, StringComparer.OrdinalIgnoreCase);
        var pool = all.Where(t => !played.Contains(t.Path)).ToList();
        var restarted = false;
        if (pool.Count == 0)
        {
            pool = all;            // the whole library was played this session - start over
            restarted = true;
        }

        var preference = _history.GenrePreferenceSince(DateTime.UtcNow.AddDays(30));
        var currentTags = ParseTags(current?.ContentTags);
        var currentFamily = GenreCatalog.Family(current?.Genre);

        Track? best = null;
        var bestScore = double.MinValue;
        string[]? bestTags = null;

        foreach (var candidate in pool)
        {
            if (current is not null && candidate.Id == current.Id) continue;

            var tags = ParseTags(candidate.ContentTags);
            var overlap = Overlap(currentTags, tags);
            var sameGenre = !string.IsNullOrWhiteSpace(candidate.Genre)
                            && string.Equals(candidate.Genre, current?.Genre, StringComparison.OrdinalIgnoreCase);
            var sameFamily = GenreCatalog.Family(candidate.Genre) == currentFamily;
            var pref = preference.GetValueOrDefault(candidate.Genre ?? "Unclassified");
            var daysSince = candidate.LastPlayedUtc is null ? 30 : (DateTime.UtcNow - candidate.LastPlayedUtc.Value).TotalDays;
            var recency = 1.0 / (1.0 + Math.Min(daysSince, 60));

            var score = 3.2 * overlap
                        + 1.4 * (sameGenre ? 1 : 0)
                        + 0.7 * (sameFamily ? 1 : 0)
                        + 0.45 * Math.Min(pref, 10)
                        + 0.35 * Math.Log(1 + candidate.PlayCount)
                        + 0.9 * recency
                        + 0.35 * Random.Shared.NextDouble();

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestTags = tags;
            }
        }

        if (best is null) return null;

        var reason = BuildReason(current, best, currentTags, bestTags ?? Array.Empty<string>(), restarted);
        return new Recommendation(best, reason);
    }

    private static string BuildReason(Track? current, Track pick, string[] currentTags, string[] pickTags, bool restarted)
    {
        if (restarted)
            return $"session finished, starting again with {pick.DisplayGenre}";

        var shared = currentTags.Intersect(pickTags, StringComparer.OrdinalIgnoreCase).ToArray();
        if (shared.Length > 0)
            return $"similar content ({string.Join(", ", shared.Take(3))})";

        if (current is not null && string.Equals(pick.Genre, current.Genre, StringComparison.OrdinalIgnoreCase))
            return $"same genre ({pick.DisplayGenre})";

        if (GenreCatalog.Family(pick.Genre) == GenreCatalog.Family(current?.Genre))
            return $"related genre ({pick.DisplayGenre})";

        return $"new direction ({pick.DisplayGenre})";
    }

    private static string[] ParseTags(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>How much of the current track's character the candidate shares (0..1).</summary>
    private static double Overlap(string[] current, string[] candidate)
    {
        if (current.Length == 0 || candidate.Length == 0) return 0;
        var set = new HashSet<string>(candidate, StringComparer.OrdinalIgnoreCase);
        var matched = current.Count(t => set.Contains(t));
        return matched / (double)current.Length;
    }
}
