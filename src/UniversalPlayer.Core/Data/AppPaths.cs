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
    public const string ModelsFolderName = "models";
    public const string ContentModelFileName = "content_model.json";

    /// <summary>Folder that contains the running executable.</summary>
    public static string ExeDirectory =>
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static string DatabasePath => Path.Combine(ExeDirectory, DatabaseFileName);
    public static string SettingsPath => Path.Combine(ExeDirectory, SettingsFileName);
    public static string LogPath => Path.Combine(ExeDirectory, LogFileName);

    /// <summary>Folder next to the executable where local analysis models live.</summary>
    public static string ModelsDirectory => Path.Combine(ExeDirectory, ModelsFolderName);
    public static string ContentModelPath => Path.Combine(ModelsDirectory, ContentModelFileName);

    /// <summary>
    /// Turns an absolute library root into a form that keeps working after the whole bundle
    /// folder (music + player) is moved: when the root is the executable's folder or one of
    /// its ancestors - the normal layout is &lt;root&gt;\UniversalPlayer\UniversalPlayer.exe -
    /// it is stored relative to the executable; any other location stays absolute.
    /// </summary>
    public static string MakePortableRoot(string absoluteRoot)
    {
        if (string.IsNullOrWhiteSpace(absoluteRoot))
            return string.Empty;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteRoot));
            var relative = Path.GetRelativePath(ExeDirectory, root);
            return Path.IsPathRooted(relative) ? root : relative;
        }
        catch
        {
            return absoluteRoot;
        }
    }

    /// <summary>Resolves a stored (relative or absolute) library root back to an absolute path.</summary>
    public static string ResolveRoot(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return DetectDefaultLibraryRoot();
        try
        {
            var combined = Path.IsPathRooted(stored) ? stored! : Path.Combine(ExeDirectory, stored);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
        }
        catch
        {
            return stored!;
        }
    }

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
