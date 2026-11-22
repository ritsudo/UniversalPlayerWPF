using UniversalPlayer.Core.Genres;

namespace UniversalPlayer.Classification.Content;

/// <summary>Combines the acoustic analysis tags with the era/style tags that only the
/// metadata knows (e.g. genre Rock + year 1973 -&gt; "70s rock").</summary>
public static class ContentTagComposer
{
    public static string Compose(string? analysedTags, int? year, string? genre)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(analysedTags))
            tags.AddRange(analysedTags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        tags.AddRange(EraTags(year, genre));
        return string.Join(";", tags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EraTags(int? year, string? genre)
    {
        if (year is not int y || y < 1950 || y > 2035)
            yield break;

        var decade = y / 10 * 10;
        var label = decade >= 2000 ? $"{decade % 100:00}s" : $"{decade % 100}s";
        yield return label;

        var style = Style(GenreCatalog.Family(genre));
        if (style is not null)
            yield return $"{label} {style}";
    }

    private static string? Style(string family) => family switch
    {
        "Rock" => "rock",
        "Electronic" => "electronic",
        "Urban" => "urban",
        "JazzBlues" => "jazz",
        "Classical" => "classical",
        "Folk" => "folk",
        "Unknown" => null,
        _ => family.ToLowerInvariant()
    };
}
