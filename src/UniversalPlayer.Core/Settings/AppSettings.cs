namespace UniversalPlayer.Core.Settings;

public enum RepeatMode { Off, All, One }

/// <summary>User preferences persisted as JSON next to the executable.</summary>
public sealed class AppSettings
{
    public string LibraryRoot { get; set; } = string.Empty;
    public double Volume { get; set; } = 0.8;
    public int RepeatMode { get; set; }
    public bool Shuffle { get; set; }
    public bool NormalizeVolume { get; set; }
    public bool AutoRecommend { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool OnlineClassification { get; set; } = true;
    public bool LocalModelFallback { get; set; } = true;
    public bool FullRescan { get; set; }
    public int HistoryRetentionDays { get; set; } = 31;
    public DateTime? LastScanUtc { get; set; }
    public string? LastTrackPath { get; set; }
    public bool LibraryViewDetails { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
