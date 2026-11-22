namespace UniversalPlayer.Core.Models;

/// <summary>A single audio file in the library.</summary>
public sealed class Track
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public GenreSource GenreSource { get; set; } = GenreSource.None;
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public long DurationMs { get; set; }
    public int? Bitrate { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public long SizeBytes { get; set; }
    public DateTime FileModifiedUtc { get; set; }
    public DateTime AddedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? ClassifiedUtc { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public bool Missing { get; set; }

    /// <summary>Descriptive content tags produced by the analysis pass, ';'-separated
    /// (e.g. "piano;calm;instrumental"); editable by hand.</summary>
    public string? ContentTags { get; set; }

    /// <summary>When the content analysis last ran; null means "unanalysed".</summary>
    public DateTime? AnalysedUtc { get; set; }

    public double? AnalysisConfidence { get; set; }

    public bool IsAnalysed => AnalysedUtc is not null;
    public string DisplayTags => string.IsNullOrWhiteSpace(ContentTags) ? (IsAnalysed ? "-" : "...") : ContentTags!;

    /// <summary>Lower-cased, denormalised blob used for fast, case-insensitive (incl. Cyrillic) search.</summary>
    public string SearchText { get; set; } = string.Empty;

    public string BuildSearchText()
    {
        SearchText = string.Join('\n', new[] { Title, Artist, Album, Genre, FileName, Folder }
            .Where(s => !string.IsNullOrWhiteSpace(s)))
            .ToLowerInvariant();
        return SearchText;
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FileName : Title;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist!;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "-" : Album!;
    public string DisplayGenre => string.IsNullOrWhiteSpace(Genre) ? "Unclassified" : Genre!;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>An artist aggregate with a cached "main genre".</summary>
public sealed class Artist
{
    public string Name { get; set; } = string.Empty;
    public string? Genre { get; set; }
    public GenreSource GenreSource { get; set; } = GenreSource.None;
    public DateTime? UpdatedUtc { get; set; }
    public int TrackCount { get; set; }
    public int PlayCount { get; set; }
}

/// <summary>One listening session entry.</summary>
public sealed class HistoryEntry
{
    /// <summary>0 = a played track, 1 = a "new session" divider.</summary>
    public int Kind { get; set; }

    public long Id { get; set; }
    public long? TrackId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Genre { get; set; }
    public DateTime StartedUtc { get; set; }
    public int PlayedSeconds { get; set; }

    public bool IsSessionStart => Kind == 1;
    public string DisplayTitle => IsSessionStart
        ? "-- new listening session --"
        : (string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileName(Path) : Title);
    public string DisplayArtist => IsSessionStart ? string.Empty : Artist ?? "Unknown artist";
    public string DisplayGenre => IsSessionStart ? string.Empty : Genre ?? "-";
    public string DisplayPlayed => IsSessionStart || PlayedSeconds <= 0 ? string.Empty : PlayedSeconds.ToString();
}

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public int ItemCount { get; set; }
}

public sealed class PlaylistItem
{
    public long Id { get; set; }
    public long PlaylistId { get; set; }
    public long TrackId { get; set; }
    public int Position { get; set; }
}

/// <summary>Summary of a library scan.</summary>
public sealed class ScanReport
{
    public int Scanned { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Missing { get; set; }
    public int Failed { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<string> Errors { get; } = new();
}
