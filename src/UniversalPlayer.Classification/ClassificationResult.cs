using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Classification;

public sealed class ClassificationResult
{
    public string? Genre { get; init; }
    public GenreSource Source { get; init; } = GenreSource.None;
    public double Confidence { get; init; }
    public string? Detail { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Genre);

    public static ClassificationResult Empty(string? detail = null) =>
        new() { Genre = null, Source = GenreSource.None, Detail = detail };
}
