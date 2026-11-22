using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalPlayer.App.Services;
using UniversalPlayer.Classification;
using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Models;
using UniversalPlayer.Core.Settings;

namespace UniversalPlayer.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TrackRepository _tracks;
    private readonly ArtistRepository _artists;
    private readonly PlaylistRepository _playlists;
    private readonly SettingsStore _settingsStore;
    private readonly LibraryService _libraryService;
    private readonly RecommendationService _recommendation;
    private readonly HistoryService _history;
    private readonly AudioPlayerService _player;

    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _positionTimer;
    private readonly HashSet<string> _sessionPlayed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCts;
    private int _currentIndex = -1;
    private int _historyTouchCounter;
    private bool _suppressPositionUpdate;

    public AppSettings Settings { get; }

    public ObservableCollection<Track> Library { get; } = new();
    public ObservableCollection<Track> Queue { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();
    public ObservableCollection<Track> PlaylistTracks { get; } = new();
    public ObservableCollection<Artist> Artists { get; } = new();
    public ObservableCollection<string> Genres { get; } = new();

    public event Action? CurrentTrackChanged;
    public event Action? RequestExit;
    public event Action? RequestShow;

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 100;
    [ObservableProperty] private bool _isProgressIndeterminate;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _genreFilter = "All";
    [ObservableProperty] private string _libraryCountText = string.Empty;
    [ObservableProperty] private string _pendingClassifyText = string.Empty;

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (SetProperty(ref _positionSeconds, value) && !_suppressPositionUpdate)
                _player.Seek(TimeSpan.FromSeconds(value));
        }
    }
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _volume = 80;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private int _repeatMode;
    [ObservableProperty] private bool _autoRecommend = true;
    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private Track? _selectedQueueTrack;
    [ObservableProperty] private Playlist? _selectedPlaylist;
    [ObservableProperty] private Artist? _selectedArtist;
    [ObservableProperty] private string _newPlaylistName = string.Empty;

    [ObservableProperty] private bool _isAnalysing;
    [ObservableProperty] private bool _analyseOnlyUnanalysed = true;
    [ObservableProperty] private string _analysisStatusText = string.Empty;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _editGenre = string.Empty;
    [ObservableProperty] private string _editTags = string.Empty;

    public string RepeatLabel => RepeatMode switch { 1 => "Repeat one", 2 => "Repeat all", _ => "Repeat off" };

    public string PlayPauseGlyph => IsPlaying ? "\u275A\u275A" : "\u25B6";

    public MainViewModel(
        TrackRepository tracks,
        ArtistRepository artists,
        PlaylistRepository playlists,
        SettingsStore settingsStore,
        LibraryService libraryService,
        RecommendationService recommendation,
        HistoryService history,
        AudioPlayerService player)
    {
        _tracks = tracks;
        _artists = artists;
        _playlists = playlists;
        _settingsStore = settingsStore;
        _libraryService = libraryService;
        _recommendation = recommendation;
        _history = history;
        _player = player;

        Settings = settingsStore.Load();
        _volume = Settings.Volume * 100;
        _shuffle = Settings.Shuffle;
        _repeatMode = Settings.RepeatMode;
        _autoRecommend = Settings.AutoRecommend;
        _player.Volume = Settings.Volume;

        _libraryService.Status += text => OnUi(() => StatusText = text);
        _libraryService.Progress += (done, total) => OnUi(() =>
        {
            ProgressMax = Math.Max(1, total);
            ProgressValue = done;
        });
        _player.TrackEnded += (_, _) => OnUi(OnTrackEnded);

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshLibrary(); };

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => Tick();
        _positionTimer.Start();
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Load()
    {
        var purged = _history.PurgeOlderThan(Settings.HistoryRetentionDays);
        _history.MarkSessionStart();
        RefreshAll();
        if (purged > 0)
            StatusText = $"Removed {purged} history entries older than {Settings.HistoryRetentionDays} days.";

        // Repair tags that were stored with the wrong code page (Cyrillic shown as "Ãîðîä").
        Task.Run(() =>
        {
            try
            {
                var fixedCount = _tracks.RepairTextFields();
                if (fixedCount > 0)
                    OnUi(() =>
                    {
                        RefreshLibrary();
                        StatusText = $"Repaired {fixedCount} track name(s) with broken Cyrillic encoding.";
                    });
            }
            catch
            {
                // repair is best effort
            }
        });
    }

    public void RefreshAll()
    {
        RefreshGenres();
        RefreshLibrary();
        RefreshArtists();
        RefreshPlaylists();
        RefreshHistory();
        RefreshPending();
        RefreshAnalysisStatus();
    }

    private void Tick()
    {
        _suppressPositionUpdate = true;
        try
        {
            PositionSeconds = _player.Position.TotalSeconds;
            if (_player.Duration.TotalSeconds > 0)
                DurationSeconds = _player.Duration.TotalSeconds;
        }
        finally
        {
            _suppressPositionUpdate = false;
        }

        var playing = _player.IsPlaying;
        if (playing != IsPlaying) IsPlaying = playing;

        if (playing && ++_historyTouchCounter >= 60) // ~15 s
        {
            _historyTouchCounter = 0;
            _history.Touch();
        }
    }

    // ---------------- library ----------------

    private void RefreshLibrary()
    {
        var genre = GenreFilter == "All" ? null : GenreFilter;
        var list = _tracks.Search(SearchText, genre, 20000);
        Library.Clear();
        foreach (var t in list) Library.Add(t);
        LibraryCountText = $"{list.Count} track(s)";
    }

    private void RefreshGenres()
    {
        var histogram = _tracks.GenreHistogram();
        var current = GenreFilter;
        Genres.Clear();
        Genres.Add("All");
        foreach (var g in histogram.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) Genres.Add(g);
        if (!Genres.Contains(current)) GenreFilter = "All";
    }

    private void RefreshArtists()
    {
        Artists.Clear();
        foreach (var a in _artists.GetAllWithCounts()) Artists.Add(a);
    }

    private void RefreshPlaylists()
    {
        var selectedId = SelectedPlaylist?.Id;
        Playlists.Clear();
        foreach (var p in _playlists.GetAll()) Playlists.Add(p);
        if (selectedId is not null)
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == selectedId);
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var h in _history.Recent(500)) History.Add(h);
    }

    private void RefreshPending()
    {
        var n = _tracks.CountUnclassified();
        var total = _tracks.Count();
        PendingClassifyText = n == 0 ? $"{total} tracks, all classified" : $"{n} of {total} track(s) unclassified";
    }

    private void RefreshAnalysisStatus()
    {
        var analysed = _tracks.CountAnalysed();
        var pending = _tracks.CountUnanalysed();
        AnalysisStatusText = $"{analysed} analysed, {pending} unanalysed";
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnGenreFilterChanged(string value) => RefreshLibrary();
    partial void OnVolumeChanged(double value)
    {
        _player.Volume = value / 100.0;
        Settings.Volume = value / 100.0;
        SaveSettings();
    }
    partial void OnShuffleChanged(bool value) { Settings.Shuffle = value; SaveSettings(); }
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseGlyph));
    partial void OnRepeatModeChanged(int value) { Settings.RepeatMode = value; OnPropertyChanged(nameof(RepeatLabel)); SaveSettings(); }
    partial void OnAutoRecommendChanged(bool value) { Settings.AutoRecommend = value; SaveSettings(); }

    partial void OnSelectedPlaylistChanged(Playlist? value) => ShowHistoryForPlaylist(value);

    partial void OnSelectedTrackChanged(Track? value)
    {
        EditGenre = value?.Genre ?? string.Empty;
        EditTags = value?.ContentTags ?? string.Empty;
        HasSelection = value is not null;
    }

    public void SaveSettings()
    {
        Settings.LibraryRoot = LibraryRoot;
        Settings.MinimizeToTray = MinimizeToTray;
        Settings.OnlineClassification = OnlineClassification;
        Settings.LocalModelFallback = LocalModelFallback;
        _settingsStore.Save(Settings);
    }

    // ---------------- settings projection ----------------

    public string LibraryRoot
    {
        get => Settings.LibraryRoot;
        set { if (Settings.LibraryRoot != value) { Settings.LibraryRoot = value; OnPropertyChanged(); } }
    }

    public bool OnlineClassification
    {
        get => Settings.OnlineClassification;
        set { if (Settings.OnlineClassification != value) { Settings.OnlineClassification = value; OnPropertyChanged(); SaveSettings(); } }
    }

    public bool LocalModelFallback
    {
        get => Settings.LocalModelFallback;
        set { if (Settings.LocalModelFallback != value) { Settings.LocalModelFallback = value; OnPropertyChanged(); SaveSettings(); } }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set { if (Settings.MinimizeToTray != value) { Settings.MinimizeToTray = value; OnPropertyChanged(); SaveSettings(); } }
    }

    public int HistoryRetentionDays
    {
        get => Settings.HistoryRetentionDays;
        set { if (value < 1) value = 1; if (Settings.HistoryRetentionDays != value) { Settings.HistoryRetentionDays = value; OnPropertyChanged(); SaveSettings(); } }
    }

    // ---------------- commands ----------------

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = false;
        try
        {
            var ct = CancellationToken.None;
            await _libraryService.RescanAsync(LibraryRoot, ct);
            if (OnlineClassification)
            {
                var revisited = _tracks.ResetLocalModelClassifications();
                if (revisited > 0)
                    StatusText = $"Re-checking {revisited} rough guess(es) online ...";
            }
            await RunClassificationAsync(ct);
            RefreshAll();
            StatusText = "Library rescan complete.";
        }
        catch (Exception ex)
        {
            StatusText = "Rescan failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = false;
        try
        {
            await RunClassificationAsync(CancellationToken.None);
            RefreshGenres();
            RefreshLibrary();
            RefreshArtists();
            RefreshPending();
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
        }
    }

    private Task RunClassificationAsync(CancellationToken ct)
    {
        var options = new ClassificationOptions
        {
            UseOnline = OnlineClassification,
            UseLocalModel = LocalModelFallback,
            UseId3 = true
        };
        return _libraryService.ClassifyPendingAsync(options, ct);
    }

    /// <summary>Start the experimental content analysis (stoppable).</summary>
    [RelayCommand]
    private async Task AnalyzeContentAsync()
    {
        if (IsBusy || IsAnalysing) return;
        _analysisCts = new CancellationTokenSource();
        IsAnalysing = true;
        IsBusy = true;
        IsProgressIndeterminate = false;
        try
        {
            await _libraryService.AnalyzeContentAsync(AnalyseOnlyUnanalysed, _analysisCts.Token);
            RefreshLibrary();
            RefreshAnalysisStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Content analysis stopped.";
            RefreshAnalysisStatus();
        }
        catch (Exception ex)
        {
            StatusText = "Analysis failed: " + ex.Message;
        }
        finally
        {
            IsAnalysing = false;
            IsBusy = false;
            ProgressValue = 0;
            _analysisCts?.Dispose();
            _analysisCts = null;
        }
    }

    [RelayCommand]
    private void StopAnalysis()
    {
        _analysisCts?.Cancel();
        StatusText = "Stopping analysis ...";
    }

    [RelayCommand]
    private void SaveTrackEdits()
    {
        if (SelectedTrack is null) return;
        var id = SelectedTrack.Id;
        var title = SelectedTrack.DisplayTitle;
        _tracks.SetGenreManual(id, EditGenre);
        _tracks.UpdateContentTags(id, EditTags);
        RefreshGenres();
        RefreshLibrary();
        RefreshAnalysisStatus();
        SelectedTrack = Library.FirstOrDefault(t => t.Id == id);
        StatusText = $"Saved details for '{title}'.";
    }

    [RelayCommand]
    private void ClearTrackTags() => EditTags = string.Empty;

    [RelayCommand]
    private void PlayPause()
    {
        if (CurrentTrack is null)
        {
            if (Library.Count > 0) PlayFromList(Library, 0);
            return;
        }
        _player.TogglePlayPause();
        IsPlaying = _player.IsPlaying;
    }

    [RelayCommand]
    private void Stop()
    {
        _player.Stop();
        _history.End();
        IsPlaying = false;
        PositionSeconds = 0;
    }

    [RelayCommand]
    private void Next()
    {
        if (Queue.Count == 0) return;
        // With "Next song: similar" on, the button forces the same similar-track choice
        // the player would make when the song ends; otherwise it is a normal queue step.
        if (AutoRecommend && TryPlaySimilar()) return;
        Advance(auto: false);
    }

    [RelayCommand]
    private void Previous()
    {
        if (Queue.Count == 0) return;
        if (_player.Position.TotalSeconds > 4)
        {
            _player.Seek(TimeSpan.Zero);
            return;
        }
        var index = _currentIndex - 1;
        if (index < 0) index = Queue.Count - 1;
        PlayIndex(index);
    }

    [RelayCommand]
    private void PlayAll() => PlayFromList(Library, 0);

    [RelayCommand]
    private void PlayTrack(Track? track)
    {
        if (track is null) return;
        var index = Library.IndexOf(track);
        PlayFromList(Library, Math.Max(0, index));
    }

    [RelayCommand]
    private void AddToQueue(Track? track)
    {
        if (track is null) return;
        Queue.Add(track);
        StatusText = $"Queued: {track.DisplayTitle}";
    }

    [RelayCommand]
    private void RemoveFromQueue(Track? track)
    {
        if (track is null) return;
        var index = Queue.IndexOf(track);
        if (index < 0) return;
        Queue.RemoveAt(index);
        if (index < _currentIndex) _currentIndex--;
        else if (index == _currentIndex) _currentIndex = Math.Min(_currentIndex, Queue.Count - 1);
    }

    [RelayCommand]
    private void ClearQueue()
    {
        Queue.Clear();
        _currentIndex = -1;
    }

    [RelayCommand]
    private void CycleRepeat() => RepeatMode = (RepeatMode + 1) % 3;

    [RelayCommand]
    private void PlayQueueItem(Track? track)
    {
        if (track is null) return;
        var index = Queue.IndexOf(track);
        if (index >= 0) PlayIndex(index);
    }

    [RelayCommand]
    private void CreatePlaylist()
    {
        var name = NewPlaylistName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        _playlists.Create(name);
        NewPlaylistName = string.Empty;
        RefreshPlaylists();
    }

    [RelayCommand]
    private void DeletePlaylist(Playlist? playlist)
    {
        if (playlist is null) return;
        _playlists.Delete(playlist.Id);
        PlaylistTracks.Clear();
        RefreshPlaylists();
    }

    [RelayCommand]
    private void AddToPlaylist(Playlist? playlist)
    {
        if (playlist is null || SelectedTrack is null) return;
        _playlists.AddTrack(playlist.Id, SelectedTrack.Id);
        RefreshPlaylists();
        StatusText = $"Added '{SelectedTrack.DisplayTitle}' to '{playlist.Name}'.";
    }

    [RelayCommand]
    private void PlayPlaylist(Playlist? playlist)
    {
        if (playlist is null) return;
        var list = _playlists.GetTracks(playlist.Id);
        if (list.Count == 0) return;
        PlayFromList(list, 0);
    }

    [RelayCommand]
    private void RemoveFromPlaylist(Track? track)
    {
        if (SelectedPlaylist is null || track is null) return;
        var items = _playlists.GetTracks(SelectedPlaylist.Id);
        var index = items.FindIndex(t => t.Id == track.Id);
        if (index >= 0)
        {
            PlaylistTracks.Remove(track);
            StatusText = "Removed from playlist.";
        }
    }

    [RelayCommand]
    private void PlayArtist(Artist? artist)
    {
        if (artist is null) return;
        var list = _tracks.Search(null, null, 100000).Where(t => string.Equals(t.Artist, artist.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) return;
        PlayFromList(list, 0);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _history.Clear();
        RefreshHistory();
        StatusText = "History cleared.";
    }

    [RelayCommand]
    private void Refresh() => RefreshAll();

    [RelayCommand]
    private void Exit() => RequestExit?.Invoke();

    [RelayCommand]
    private void ShowWindow() => RequestShow?.Invoke();

    // ---------------- playback engine ----------------

    public void PlayFromList(IList<Track> list, int startIndex)
    {
        if (list.Count == 0) return;
        Queue.Clear();
        foreach (var t in list) Queue.Add(t);
        PlayIndex(Math.Clamp(startIndex, 0, Queue.Count - 1));
    }

    private void PlayIndex(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        _currentIndex = index;
        var track = Queue[index];

        if (!_player.PlayFile(track.Path))
        {
            StatusText = $"Cannot play: {track.FileName}";
            _player.Stop();
            Advance(auto: true);
            return;
        }

        _history.End();
        CurrentTrack = track;
        IsPlaying = true;
        DurationSeconds = _player.Duration.TotalSeconds;
        PositionSeconds = 0;

        _history.Start(track);
        _tracks.IncrementPlay(track.Id);
        track.PlayCount++;
        track.LastPlayedUtc = DateTime.UtcNow;

        _sessionPlayed.Add(track.Path);

        StatusText = $"Playing: {track.DisplayTitle} - {track.DisplayArtist}";
        CurrentTrackChanged?.Invoke();
        RefreshHistory();
    }

    private void OnTrackEnded()
    {
        _history.End();
        if (RepeatMode == 1 && _currentIndex >= 0 && _currentIndex < Queue.Count)
        {
            PlayIndex(_currentIndex);
            return;
        }
        Advance(auto: true);
    }

    private void Advance(bool auto)
    {
        if (Queue.Count == 0)
        {
            Stop();
            return;
        }

        // "Next song: similar" - pick the most similar track still unplayed this session,
        // so a session turns into thematic runs (piano -> piano, classical -> classical, ...).
        if (auto && AutoRecommend && TryPlaySimilar())
            return;

        int next;
        if (Shuffle && Queue.Count > 1)
        {
            do { next = Random.Shared.Next(Queue.Count); } while (next == _currentIndex);
        }
        else
        {
            next = _currentIndex + 1;
            if (next >= Queue.Count)
            {
                if (RepeatMode == 2 || !auto) next = 0;
                else next = -1;
            }
        }

        if (next >= 0 && next < Queue.Count)
        {
            PlayIndex(next);
            return;
        }

        Stop();
    }

    /// <summary>Plays the most similar unplayed track; returns false when there is none.</summary>
    private bool TryPlaySimilar()
    {
        var pick = _recommendation.RecommendNext(CurrentTrack, _sessionPlayed);
        if (pick is null) return false;

        Queue.Add(pick.Track);
        StatusText = $"Next similar ({pick.Reason}): {pick.Track.DisplayTitle}";
        PlayIndex(Queue.Count - 1);
        return true;
    }

    public void ShowHistoryForPlaylist(Playlist? playlist)
    {
        PlaylistTracks.Clear();
        if (playlist is null) return;
        foreach (var t in _playlists.GetTracks(playlist.Id)) PlaylistTracks.Add(t);
    }
}
