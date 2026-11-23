using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
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
        args.Length > 0 && args[0].ToLowerInvariant() is "--scan" or "--classify" or "--analyse" or "--analyze" or "--fix-names" or "--normalize" or "--dump-audio" or "--stats" or "--test-audio" or "--test-online" or "--help" or "-h" or "/?";

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
                    Console.WriteLine("  --scan      rescan the library and classify new tracks (fast: only changed folders)");
                    Console.WriteLine("  --full-scan with --scan: re-check every folder instead of only changed ones");
                    Console.WriteLine("  --classify  classify tracks that have no genre yet");
                    Console.WriteLine("  --analyse   run the content analysis on unanalysed tracks (--all to redo, --limit n)");
                    Console.WriteLine("  --fix-names repair track names/tags stored with the wrong code page");
                    Console.WriteLine("  --normalize measure volume-normalisation gains (--limit n)");
                    Console.WriteLine("  --dump-audio --file <path> [--seconds n] [--seek s] [--out wav]  decode to a wav (diagnostics)");
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

                case "--dump-audio":
                {
                    var filePath = StringArg(args, "--file") ?? Environment.GetEnvironmentVariable("UP_TEST_FILE");
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        Console.WriteLine("--dump-audio requires --file <path>");
                        return 2;
                    }
                    var seconds = ParseIntArg(args, "--seconds", 25);
                    var seek = ParseIntArg(args, "--seek", 0);
                    var gainArg = StringArg(args, "--gain");
                    var gain = double.TryParse(gainArg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 1.0;
                    var outPath = StringArg(args, "--out") ?? Path.Combine(Path.GetTempPath(), "up_dump.wav");

                    using var reader = UniversalPlayer.Classification.Audio.AudioStreamFactory.Open(filePath!);
                    if (seek > 0)
                    {
                        reader.CurrentTime = TimeSpan.FromSeconds(seek);
                        Console.WriteLine($"seeked to {seek}s -> position {reader.CurrentTime}");
                    }

                    // mirror the player's DSP: normalisation gain, then the soft limiter
                    NAudio.Wave.ISampleProvider provider = reader.ToSampleProvider();
                    if (Math.Abs(gain - 1.0) > 1e-6)
                    {
                        provider = new NAudio.Wave.SampleProviders.VolumeSampleProvider(provider) { Volume = (float)gain };
                        provider = new UniversalPlayer.Classification.Audio.SoftLimiterSampleProvider(provider);
                    }

                    var wave = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(provider);
                    using (var writer = new NAudio.Wave.WaveFileWriter(outPath, wave.WaveFormat))
                    {
                        var buffer = new byte[Math.Max(4096, wave.WaveFormat.AverageBytesPerSecond / 2)];
                        var target = (long)seconds * wave.WaveFormat.AverageBytesPerSecond;
                        long total = 0;
                        while (total < target)
                        {
                            var read = wave.Read(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            var take = (int)Math.Min(read, target - total);
                            writer.Write(buffer, 0, take);
                            total += take;
                        }
                        writer.Flush();
                        Console.WriteLine($"wrote {total} bytes (gain {gain:0.###}) to {outPath}");
                    }
                    return 0;
                }

                case "--normalize":
                {
                    var dbN = Open(settings);
                    var tracksN = new TrackRepository(dbN);
                    var normalizer = new UniversalPlayer.Classification.Audio.TrackNormalizer(TimeSpan.FromSeconds(15));
                    var limitN = ParseIntArg(args, "--limit", int.MaxValue);

                    var pending = tracksN.Search(null, null, 200_000)
                        .Where(t => t.NormGain is null)
                        .Take(limitN)
                        .ToList();

                    Console.WriteLine($"Measuring {pending.Count} track(s) ...");
                    var doneN = 0;
                    var boosted = 0;
                    foreach (var track in pending)
                    {
                        var gain = normalizer.Measure(track.Path, CancellationToken.None);
                        tracksN.SetNormGain(track.Id, gain);
                        doneN++;
                        if (gain > 1.02) boosted++;
                        if (doneN % 25 == 0 || doneN == pending.Count)
                            Console.WriteLine($"  {doneN}/{pending.Count} measured ({boosted} amplified)");
                    }
                    Console.WriteLine($"Done. {doneN} measured, {boosted} amplified.");
                    return 0;
                }

                case "--fix-names":
                {
                    var dbF = Open(settings);
                    var tracksF = new TrackRepository(dbF);
                    var fixedCount = tracksF.RepairTextFields();
                    Console.WriteLine($"Repaired text in {fixedCount} track(s).");
                    return 0;
                }

                case "--analyse":
                case "--analyze":
                {
                    var dbA = Open(settings);
                    var tracksA = new TrackRepository(dbA);
                    var analyzer = new UniversalPlayer.Classification.Content.ContentAnalyzer();
                    var onlyUnanalysed = !args.Any(a => a.Equals("--all", StringComparison.OrdinalIgnoreCase));
                    var limitA = ParseIntArg(args, "--limit", int.MaxValue);

                    var allTracks = tracksA.Search(null, null, 200_000);
                    var todo = (onlyUnanalysed ? allTracks.Where(t => !t.IsAnalysed) : allTracks).Take(limitA).ToList();
                    if (analyzer.NeedsCalibration)
                    {
                        Console.WriteLine("Calibrating the content model on a sample of the collection ...");
                        analyzer.Calibrate(allTracks.Select(t => t.Path).ToList(), 150, CancellationToken.None);
                    }
                    Console.WriteLine($"Analysing {todo.Count} track(s) ...");
                    var doneA = 0;
                    foreach (var track in todo)
                    {
                        var res = analyzer.Analyze(track.Path, CancellationToken.None);
                        var tags = UniversalPlayer.Classification.Content.ContentTagComposer.Compose(res?.Tags, track.Year, track.Genre);
                        tracksA.SetAnalysis(track.Id, tags, res?.Confidence ?? 0);
                        doneA++;
                        if (doneA % 5 == 0 || doneA == todo.Count)
                            Console.WriteLine($"  [{doneA}/{todo.Count}] {track.DisplayArtist} - {track.DisplayTitle} => {tags}");
                    }
                    Console.WriteLine($"Done. Analysed {doneA} track(s).");
                    return 0;
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
                        var fastScan = !args.Any(a => a.Equals("--full-scan", StringComparison.OrdinalIgnoreCase));
                        Console.WriteLine();
                        Console.WriteLine($"{(fastScan ? "Fast" : "Full")} scan of {settings.LibraryRoot} ...");
                        var scanner = new Core.Library.LibraryScanner(db, tracks, new ScanFolderRepository(db));
                        var report = scanner.Scan(settings.LibraryRoot, fastScan, CancellationToken.None);
                        Console.WriteLine(report.Summary);
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
