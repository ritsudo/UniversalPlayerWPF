using System.Text.RegularExpressions;

namespace UniversalPlayer.Classification;

/// <summary>Strips video-clip noise ("(Official HD Video)", "LYRICS", "4K remaster") from titles
/// before they are sent to the online genre databases.</summary>
internal static partial class TitleCleaner
{
    private static readonly string[] NoiseWords =
    {
        "official", "officiel", "lyrics", "lyric", "video", "videoclip", "audio", "hd", "hq",
        "4k", "1080p", "720p", "remaster", "remastered", "mv", "m/v", "explicit", "music",
        "clip", "full", "version", "live", "hqaudio", "visualizer", "prod"
    };

    public static string Clean(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;

        var text = Regex.Replace(title, @"[\(\[\{][^\)\]\}]*[\)\]\}]", " ");
        foreach (var word in NoiseWords)
            text = Regex.Replace(text, $@"\b{Regex.Escape(word)}\b", " ", RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '_', '.', ',', '|', '/');

        return text.Length < 2 ? title : text;
    }
}
