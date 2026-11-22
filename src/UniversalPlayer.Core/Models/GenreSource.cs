namespace UniversalPlayer.Core.Models;

/// <summary>Where a track/artist genre tag came from, in order of trustworthiness.</summary>
public enum GenreSource
{
    None = 0,
    Id3 = 1,
    OnlineTrack = 2,
    OnlineArtist = 3,
    LocalModel = 4,
    Manual = 5
}
