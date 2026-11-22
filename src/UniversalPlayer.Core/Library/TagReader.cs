using UniversalPlayer.Core.Genres;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Core.Library;

/// <summary>
/// Reads embedded metadata (ID3v2, MP4 atoms, Vorbis comments, ...) from an audio file
/// using TagLib#. Falls back to sensible values derived from the file name when tags are empty.
/// </summary>
public static class TagReader
{
    public static Track Read(string path)
    {
        var info = new FileInfo(path);
        var track = new Track
        {
            Path = path,
            FileName = Path.GetFileNameWithoutExtension(path),
            Title = Path.GetFileNameWithoutExtension(path),
            SizeBytes = info.Exists ? info.Length : 0,
            FileModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
            AddedUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title))
                track.Title = tag.Title.Trim();

            var performer = tag.Performers?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                            ?? tag.AlbumArtists?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (!string.IsNullOrWhiteSpace(performer))
                track.Artist = performer.Trim();

            if (!string.IsNullOrWhiteSpace(tag.Album))
                track.Album = tag.Album.Trim();

            var rawGenre = tag.Genres?.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));
            if (!string.IsNullOrWhiteSpace(rawGenre))
            {
                var normalised = GenreCatalog.Normalize(rawGenre);
                if (normalised is not null)
                {
                    track.Genre = normalised;
                    track.GenreSource = GenreSource.Id3;
                    track.ClassifiedUtc = DateTime.UtcNow;
                }
                else if (!GenreCatalog.IsJunk(rawGenre))
                {
                    track.Genre = rawGenre.Trim();
                    track.GenreSource = GenreSource.Id3;
                    track.ClassifiedUtc = DateTime.UtcNow;
                }
                // junk labels are ignored so the classifier can try instead
            }

            if (tag.Year > 0) track.Year = (int)tag.Year;
            if (tag.Track > 0) track.TrackNumber = (int)tag.Track;

            var props = file.Properties;
            track.DurationMs = (long)props.Duration.TotalMilliseconds;
            if (props.AudioBitrate > 0) track.Bitrate = props.AudioBitrate;
            if (props.AudioSampleRate > 0) track.SampleRate = props.AudioSampleRate;
            if (props.AudioChannels > 0) track.Channels = props.AudioChannels;
        }
        catch
        {
            // Untagged or unreadable container (e.g. raw .mp4 whose tags are missing):
            // keep filename-based defaults; playback and classification still work.
        }

        if (string.IsNullOrWhiteSpace(track.Artist))
            track.Artist = null;
        if (string.IsNullOrWhiteSpace(track.Album))
            track.Album = null;

        // Video rips and many downloads carry no tags at all; "Artist - Title" in the file
        // name is a good enough hint for the online classifier.
        if (track.Artist is null && track.Title.Contains(" - ", StringComparison.Ordinal))
        {
            var split = track.Title.Split(new[] { " - " }, 2, StringSplitOptions.None);
            if (split[0].Length > 0 && split[1].Length > 0)
            {
                track.Artist = split[0].Trim();
                track.Title = split[1].Trim();
            }
        }

        return track;
    }
}
