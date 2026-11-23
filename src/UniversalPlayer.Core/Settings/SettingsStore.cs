using System.Text.Json;
using UniversalPlayer.Core.Data;

namespace UniversalPlayer.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public SettingsStore(string? path = null) => FilePath = path ?? AppPaths.SettingsPath;

    public AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            }
        }
        catch
        {
            // corrupted settings - fall back to defaults
        }

        settings ??= new AppSettings();

        // The stored root may be relative (so the bundle can be moved) or point at a folder
        // that no longer exists (the bundle was moved). Resolve it against the current
        // executable location and, when that fails, auto-detect the music folder again.
        var root = AppPaths.ResolveRoot(settings.LibraryRoot);
        if (!Directory.Exists(root))
            root = AppPaths.DetectDefaultLibraryRoot();
        settings.LibraryRoot = root;
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Persist the root relative to the executable whenever possible so moving the
            // whole bundle folder keeps pointing at the same (moved) music library.
            var portable = settings.Clone();
            portable.LibraryRoot = AppPaths.MakePortableRoot(settings.LibraryRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(portable, Options));
        }
        catch
        {
            // saving is best-effort
        }
    }
}
