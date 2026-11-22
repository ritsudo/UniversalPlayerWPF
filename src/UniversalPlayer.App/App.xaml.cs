using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using UniversalPlayer.App.Services;
using UniversalPlayer.App.ViewModels;
using UniversalPlayer.Classification;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Settings;

namespace UniversalPlayer.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayService? _tray;
    private MainWindow? _window;

    public MainViewModel? ViewModel { get; private set; }

    public static bool ExitRequested { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();

        if (CliRunner.IsCliMode(e.Args))
        {
            var code = CliRunner.Run(e.Args, settingsStore, settings);
            Shutdown(code);
            return;
        }

        _mutex = new Mutex(true, "UniversalPlayer.SingleInstance.v1", out var created);
        if (!created)
        {
            MessageBox.Show("Universal Player is already running (check the system tray).", "Universal Player",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception.ToString());
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "Universal Player error", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        var db = new Database(AppPaths.DatabasePath);
        db.Initialize();

        var tracks = new TrackRepository(db);
        var artists = new ArtistRepository(db);
        var playlists = new PlaylistRepository(db);
        var historyRepo = new HistoryRepository(db);
        var classifier = ClassificationService.CreateDefault();
        var content = new UniversalPlayer.Classification.Content.ContentAnalyzer();
        var library = new LibraryService(db, tracks, artists, classifier, content);
        var recommendation = new RecommendationService(tracks, historyRepo);
        var history = new HistoryService(historyRepo);
        var player = new AudioPlayerService();

        ViewModel = new MainViewModel(tracks, artists, playlists, settingsStore, library, recommendation, history, player);
        ViewModel.RequestExit += () =>
        {
            ExitRequested = true;
            Shutdown();
        };
        ViewModel.RequestShow += ShowMainWindow;

        _window = new MainWindow { DataContext = ViewModel };
        MainWindow = _window;

        _tray = new TrayService();
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.Initialize(LoadTrayIcon(), (ContextMenu)Resources["TrayContextMenu"]!, ViewModel);
        ViewModel.CurrentTrackChanged += () => _tray?.SetTooltip(BuildTooltip());

        ViewModel.Load();
        _window.Show();
    }

    private string BuildTooltip()
    {
        var track = ViewModel?.CurrentTrack;
        return track is null
            ? "Universal Player"
            : $"Universal Player\n{track.DisplayTitle} - {track.DisplayArtist}";
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var stream = GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative))!.Stream;
        return new System.Drawing.Icon(stream);
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(AppPaths.LogPath, $"{DateTime.Now:u} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the app
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ExitRequested = true;
        ViewModel?.SaveSettings();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
