using System.Diagnostics;
using System.Globalization;
using UniversalPlayer.Core.Data;

namespace UniversalPlayer.Classification.Audio;

/// <summary>
/// Finds the ffmpeg / ffprobe executables. They are looked for next to the player
/// executable first (drop them in the program folder) and then on the PATH.
/// </summary>
public static class FfmpegLocator
{
    private static readonly Lazy<string?> Ffmpeg = new(() => Find("ffmpeg.exe"));
    private static readonly Lazy<string?> Ffprobe = new(() => Find("ffprobe.exe"));

    public static string? ExecutablePath => Ffmpeg.Value;
    public static string? ProbePath => Ffprobe.Value;
    public static bool IsAvailable => ExecutablePath is not null;

    private static string? Find(string fileName)
    {
        try
        {
            var local = Path.Combine(AppPaths.ExeDirectory, fileName);
            if (File.Exists(local)) return local;
        }
        catch
        {
            // ignore and fall through to PATH
        }

        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // skip invalid PATH entries
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Media length via ffprobe; TimeSpan.Zero when unknown.</summary>
    public static TimeSpan ProbeDuration(string path)
    {
        var probe = ProbePath;
        if (probe is null) return TimeSpan.Zero;

        try
        {
            var psi = new ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=nw=1:nk=1");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null) return TimeSpan.Zero;

            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
        }
        catch
        {
            // fall back to "unknown"
        }

        return TimeSpan.Zero;
    }
}
