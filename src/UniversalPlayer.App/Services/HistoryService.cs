using UniversalPlayer.Core.Data;
using UniversalPlayer.Core.Models;

namespace UniversalPlayer.App.Services;

/// <summary>
/// Records listening sessions. Every entry stores when a track started and how long it
/// actually played; entries older than the retention window (one month by default) are
/// deleted so the history stays bounded but is still deep enough to restore the order
/// of what was listened to.
/// </summary>
public sealed class HistoryService
{
    private readonly HistoryRepository _history;
    private long _currentId;
    private DateTime _startedUtc;

    public HistoryService(HistoryRepository history) => _history = history;

    /// <summary>Marks the start of a new listening session (called once when the program starts).</summary>
    public void MarkSessionStart() => _history.AddSessionStart(DateTime.UtcNow);

    public void Start(Track track)
    {
        End();
        _startedUtc = DateTime.UtcNow;
        _currentId = _history.Add(new HistoryEntry
        {
            TrackId = track.Id,
            Path = track.Path,
            Title = track.DisplayTitle,
            Artist = track.Artist,
            Genre = track.Genre,
            StartedUtc = _startedUtc,
            PlayedSeconds = 0
        });
    }

    /// <summary>Persists how long the current track has been playing (safe to call repeatedly).</summary>
    public void Touch()
    {
        if (_currentId == 0) return;
        _history.UpdatePlayedSeconds(_currentId, ElapsedSeconds());
    }

    public void End()
    {
        if (_currentId == 0) return;
        _history.UpdatePlayedSeconds(_currentId, ElapsedSeconds());
        _currentId = 0;
    }

    private int ElapsedSeconds() => Math.Max(0, (int)(DateTime.UtcNow - _startedUtc).TotalSeconds);

    public int PurgeOlderThan(int retentionDays)
    {
        if (retentionDays <= 0) retentionDays = 31;
        return _history.PurgeOlderThan(DateTime.UtcNow.AddDays(-retentionDays));
    }

    public List<HistoryEntry> Recent(int limit = 500) => _history.GetRecent(limit);
    public void Clear() => _history.Clear();
}
