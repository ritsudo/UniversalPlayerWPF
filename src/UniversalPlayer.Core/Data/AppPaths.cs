namespace UniversalPlayer.Core.Data;

using System.Collections.Generic;

/// <summary>
/// Resolves the on-disk locations used by the player. The SQLite database and the
/// settings file always live next to the executable, inside the working (music) folder.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "UniversalPlayer";
    public const string DatabaseFileName = "universal_player.db";
    public const string SettingsFileName = "universal_player.settings.json";
    public const string LogFileName = "universal_player.log";

    /// <summary>Folder that contains the running executable.</summary>
    public static string ExeDirectory =>
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static string DatabasePath => Path.Combine(ExeDirectory, DatabaseFileName);
    public static string SettingsPath => Path.Combine(ExeDirectory, SettingsFileName);
    public static string LogPath => Path.Combine(ExeDirectory, LogFileName);

    /// <summary>
    /// Best guess for the music root when no setting exists yet. If the executable sits in
    /// the music folder itself that folder is used; if it sits in a sub-folder (e.g. the
    /// published UniversalPlayer folder) the closest ancestor that actually contains music
    /// is used instead.
    /// </summary>
    public static string DetectDefaultLibraryRoot()
    {
        var exeDir = ExeDirectory;

        if (ContainsAudio(exeDir))
            return exeDir;

        var dir = Path.GetDirectoryName(exeDir);
        for (var i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
        {
            if (HasAudioBelow(dir, exeDir, maxDepth: 2))
                return dir;

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir)
                break;
            dir = parent;
        }

        return exeDir;
    }

    public static bool ContainsAudio(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(directory))
            {
                if (Library.AudioFileTypes.IsAudioFile(f))
                    return true;
            }
        }
        catch
        {
            // ignored - inaccessible folders simply do not count as a music root
        }
        return false;
    }

    /// <summary>Bounded breadth-first search for any audio file below <paramref name="root"/>.</summary>
    private static bool HasAudioBelow(string root, string excludeDir, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var inspected = 0;

        while (queue.Count > 0 && inspected < 8000)
        {
            var (current, depth) = queue.Dequeue();
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current, excludeDir, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                foreach (var f in Directory.EnumerateFiles(current))
                {
                    inspected++;
                    if (Library.AudioFileTypes.IsAudioFile(f))
                        return true;
                }
            }
            catch
            {
                // ignore
            }

            if (depth >= maxDepth)
                continue;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current))
                    queue.Enqueue((sub, depth + 1));
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }
}
