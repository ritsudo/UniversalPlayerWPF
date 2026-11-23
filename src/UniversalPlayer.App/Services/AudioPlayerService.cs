using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UniversalPlayer.App.Services;

/// <summary>
/// Audio playback built on NAudio. Everything is decoded through Windows Media Foundation,
/// which means the audio track of an .mp4 (or .m4a) is played back exactly like an .mp3 -
/// the video stream is ignored entirely.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly object _gate = new();
    private IWavePlayer? _output;
    private WaveStream? _reader;
    private VolumeSampleProvider? _volume;
    private VolumeSampleProvider? _normalize;
    private bool _stopRequested;
    private float _pendingVolume = 0.8f;
    private double _normalizeGain = 1.0;

    /// <summary>Raised (on a background thread) when a track finishes playing naturally.</summary>
    public event EventHandler? TrackEnded;
    public event EventHandler? PlaybackFailed;

    public string? CurrentPath { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public double Volume
    {
        get => _volume?.Volume ?? _pendingVolume;
        set
        {
            _pendingVolume = (float)Math.Clamp(value, 0, 1);
            if (_volume is not null) _volume.Volume = _pendingVolume;
        }
    }

    public bool PlayFile(string path, double normalizeGain = 1.0)
    {
        lock (_gate)
        {
            _stopRequested = true;
            Cleanup();
            _stopRequested = false;
            _normalizeGain = normalizeGain <= 0 ? 1.0 : normalizeGain;

            try
            {
                _reader = OpenReader(path);
                var samples = _reader.ToSampleProvider();
                _normalize = new VolumeSampleProvider(samples) { Volume = (float)_normalizeGain };
                var limited = new UniversalPlayer.Classification.Audio.SoftLimiterSampleProvider(_normalize);
                _volume = new VolumeSampleProvider(limited) { Volume = _pendingVolume };
                _output = new WaveOutEvent { DesiredLatency = 300, NumberOfBuffers = 3 };
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_volume);
                _output.Play();
                CurrentPath = path;
                return true;
            }
            catch
            {
                Cleanup();
                CurrentPath = null;
                PlaybackFailed?.Invoke(this, EventArgs.Empty);
                return false;
            }
        }
    }

    /// <summary>The normalisation gain currently applied (1.0 = off); settable while playing.</summary>
    public double NormalizeGain
    {
        get => _normalizeGain;
        set
        {
            _normalizeGain = value <= 0 ? 1.0 : value;
            if (_normalize is not null) _normalize.Volume = (float)_normalizeGain;
        }
    }

    private static WaveStream OpenReader(string path)
    {
        // Shared with the classifier: handles mp3/mp4/m4a/flac/wav and unicode paths.
        return UniversalPlayer.Classification.Audio.AudioStreamFactory.Open(path);
    }

    public void Play()
    {
        lock (_gate) _output?.Play();
    }

    public void Pause()
    {
        lock (_gate) _output?.Pause();
    }

    public void TogglePlayPause()
    {
        lock (_gate)
        {
            if (_output is null) return;
            if (_output.PlaybackState == PlaybackState.Playing) _output.Pause();
            else _output.Play();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            _output?.Stop();
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null) return;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _reader.TotalTime) position = _reader.TotalTime;
            _reader.CurrentTime = position;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        var natural = !_stopRequested && e.Exception is null;
        if (natural)
            TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Cleanup()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { /* ignore */ }
            try { _output.Dispose(); } catch { /* ignore */ }
            _output = null;
        }
        if (_reader is not null)
        {
            try { _reader.Dispose(); } catch { /* ignore */ }
            _reader = null;
        }
        _volume = null;
        _normalize = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopRequested = true;
            Cleanup();
        }
    }
}
