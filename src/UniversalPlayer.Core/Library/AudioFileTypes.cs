namespace UniversalPlayer.Core.Library;

/// <summary>Known audio container formats and helpers to classify files by extension.</summary>
public static class AudioFileTypes
{
    // Everything NAudio / Windows Media Foundation can decode on Windows 10/11.
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp4", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".wma", ".aiff", ".aif", ".alac", ".ogg", ".opus"
    };

    // Formats that carry a video stream we deliberately ignore - only the audio track is played.
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".webm" };

    public static bool IsAudioFile(string path) => Audio.Contains(Path.GetExtension(path));
    public static bool IsVideoContainer(string path) => Video.Contains(Path.GetExtension(path));

    public static IReadOnlyCollection<string> SupportedExtensions => Audio;
}
