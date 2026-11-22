using System.Runtime.InteropServices;
using UniversalPlayer.Classification;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Settings;

namespace UniversalPlayer.App.Services;

/// <summary>
/// Head-less entry points so the player can scan / classify / report from a terminal:
///   UniversalPlayer.exe --scan | --classify | --stats | --help
/// </summary>
public static class CliRunner
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static bool IsCliMode(string[] args) =>
        args.Length > 0 && args[0].ToLowerInvariant() is "--scan" or "--classify" or "--stats" or "--test-audio" or "--test-online" or "--help" or "-h" or "/?";

    public static int Run(string[] args, SettingsStore settingsStore, AppSettings settings)
    {
        if (!Console.IsOutputRedirected)
            AttachConsole(-1);

        // The classification pipeline is async; detach it from the WPF dispatcher so a
        // blocking GetAwaiter().GetResult() here can never deadlock the message pump.
        SynchronizationContext.SetSynchronizationContext(null);

        var mode = args[0].ToLowerInvariant();

        var rootIndex = Array.FindIndex(args, a => a.Equals("--root", StringComparison.OrdinalIgnoreCase));
        if (rootIndex >= 0 && rootIndex + 1 < args.Length)
        {
            settings.LibraryRoot = args[rootIndex + 1];
            settingsStore.Save(settings);
        }

        if (args.Any(a => a.Equals("--offline", StringComparison.OrdinalIgnoreCase)))
            settings.OnlineClassification = false;

        try
        {
            switch (mode)
            {
                case "--help":
                case "-h":
                case "/?":
                    Console.WriteLine();
                    Console.WriteLine("Universal Player command line:");
                    Console.WriteLine("  --scan      rescan the library and classify new tracks");
                    Console.WriteLine("  --classify  classify tracks that have no genre yet");
                    Console.WriteLine("  --stats     show library statistics");
                    Console.WriteLine("  --root <folder>  use this music folder instead of the configured one");
                    Console.WriteLine("  --test-audio --file <path>  play a file for 2 s and report the result");
                    Console.WriteLine("  --no-classify      with --scan: only scan, do not classify");
                    Console.WriteLine("  --limit <n>        with --classify: classify at most n tracks");
                    Console.WriteLine("  --offline          disable the online genre lookup");
                    return 0;

                case "--stats":
                {
                    var db = Open(settings);
                    var tracks = new TrackRepository(db);
                    var history = new HistoryRepository(db);
                    var artists = new ArtistRepository(db);
                    Console.WriteLine();
                    Console.WriteLine($"Library root : {settings.LibraryRoot}");
                    Console.WriteLine($"Database     : {AppPaths.DatabasePath}");
                    Console.WriteLine($"Tracks       : {tracks.Count()}");
                    Console.WriteLine($"Unclassified : {tracks.CountUnclassified()}");
                    Console.WriteLine($"Artists      : {artists.GetAllWithCounts().Count}");
                    Console.WriteLine($"History rows : {history.Count()}");
                    Console.WriteLine("Genres:");
                    foreach (var kv in tracks.GenreHistogram())
                        Console.WriteLine($"  {kv.Key,-20} {kv.Value}");
                    return 0;
                }

                case "--test-audio":
                {
                    var fileIdx = Array.FindIndex(args, a => a.Equals("--file", StringComparison.OrdinalIgnoreCase));
                    var file = fileIdx >= 0 && fileIdx + 1 < args.Length
                        ? args[fileIdx + 1]
                        : Environment.GetEnvironmentVariable("UP_TEST_FILE");
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        Console.WriteLine("--test-audio requires --file <path>");
                        return 2;
                    }

                    using var player = new AudioPlayerService { Volume = 0.05 };
                    var opened = player.PlayFile(file!);
                    Console.WriteLine($"opened={opened} duration={player.Duration}");
                    Thread.Sleep(2500);
                    Console.WriteLine($"position={player.Position} playing={player.IsPlaying}");
                    player.Seek(TimeSpan.FromSeconds(60));
                    Thread.Sleep(400);
                    Console.WriteLine($"after seek to 60s: position={player.Position}");
                    player.Stop();
                    return opened ? 0 : 1;
                }

                case "--test-online":
                {
                    var artist = StringArg(args, "--artist") ?? "Metallica";
                    var title = StringArg(args, "--title") ?? "Nothing Else Matters";
                    var resolver = new UniversalPlayer.Classification.Online.OnlineGenreResolver();
                    var deezer = new UniversalPlayer.Classification.Online.DeezerClient();
                    Console.WriteLine($"track lookup '{artist}' - '{title}' ...");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var rawTrack = deezer.GetTrackGenreAsync(artist, title, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"  deezer track raw = '{rawTrack.Genre}' artistMatched={rawTrack.ArtistMatched} ({sw.Elapsed.TotalSeconds:F1}s)");
                    var rawArtist = deezer.GetArtistGenreAsync(artist, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"  deezer artist raw = '{rawArtist}' ({sw.Elapsed.TotalSeconds:F1}s)");
                    var result = resolver.ResolveTrackAsync(artist, title, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"  => genre='{result.Genre}' source={result.Source} in {sw.Elapsed.TotalSeconds:F1}s ({result.Detail})");
                    return 0;
                }

                case "--scan":
                case "--classify":
                {
                    var db = Open(settings);
                    var tracks = new TrackRepository(db);
                    var artistRepo = new ArtistRepository(db);

                    if (mode == "--scan")
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Scanning {settings.LibraryRoot} ...");
                        var scanner = new Core.Library.LibraryScanner(db, tracks);
                        var report = scanner.Scan(settings.LibraryRoot, CancellationToken.None);
                        Console.WriteLine($"Scanned {report.Scanned}, added {report.Added}, updated {report.Updated}, missing {report.Missing}, failed {report.Failed} in {report.Elapsed.TotalSeconds:F1}s.");
                        foreach (var err in report.Errors) Console.WriteLine("  ! " + err);

                        if (args.Any(a => a.Equals("--no-classify", StringComparison.OrdinalIgnoreCase)))
                            return 0;
                    }

                    var classifier = ClassificationService.CreateDefault();
                    var options = new ClassificationOptions
                    {
                        UseOnline = settings.OnlineClassification,
                        UseLocalModel = settings.LocalModelFallback,
                        UseId3 = true
                    };
                    var artistCache = new Dictionary<string, Core.Models.Artist?>(StringComparer.OrdinalIgnoreCase);
                    var total = tracks.CountUnclassified();
                    var limit = ParseIntArg(args, "--limit", int.MaxValue);
                    Console.WriteLine($"Classifying {total} track(s) ...");
                    var done = 0;

                    while (done < limit)
                    {
                        var batch = tracks.GetUnclassified(Math.Min(100, limit - done));
                        if (batch.Count == 0) break;
                        foreach (var track in batch)
                        {
                            Core.Models.Artist? hint = null;
                            if (!string.IsNullOrWhiteSpace(track.Artist))
                            {
                                if (!artistCache.TryGetValue(track.Artist!, out hint))
                                {
                                    hint = artistRepo.Get(track.Artist!);
                                    artistCache[track.Artist!] = hint;
                                }
                            }

                            var classifyTask = classifier.ClassifyAsync(track, hint, options, CancellationToken.None);
                            var finished = Task.WhenAny(classifyTask, Task.Delay(TimeSpan.FromSeconds(25))).GetAwaiter().GetResult();
                            var result = finished == classifyTask
                                ? classifyTask.GetAwaiter().GetResult()
                                : ClassificationResult.Empty("timed out");
                            tracks.SetClassification(track.Id, result.Genre, result.Source);
                            if (!string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(result.Genre))
                                artistRepo.Upsert(track.Artist!, result.Genre, result.Source);

                            done++;
                            Console.WriteLine($"  [{done}/{total}] {track.DisplayArtist} - {track.DisplayTitle} => {result.Genre ?? "unknown"} ({result.Source})");
                            if (done >= limit) break;
                        }
                    }

                    Console.WriteLine($"Done. Classified {done} track(s).");
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
            return 1;
        }

        return 0;
    }

    private static string? StringArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int ParseIntArg(string[] args, string name, int fallback)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
    }

    private static Database Open(AppSettings settings)
    {
        var db = new Database(AppPaths.DatabasePath);
        db.Initialize();
        return db;
    }
}
