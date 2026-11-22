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
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.LibraryRoot))
                        settings.LibraryRoot = AppPaths.DetectDefaultLibraryRoot();
                    return settings;
                }
            }
        }
        catch
        {
            // corrupted settings - fall back to defaults
        }

        return new AppSettings { LibraryRoot = AppPaths.DetectDefaultLibraryRoot() };
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // saving is best-effort
        }
    }
}
