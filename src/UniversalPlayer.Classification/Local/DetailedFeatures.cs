namespace UniversalPlayer.Classification.Local;

/// <summary>A richer feature set used by the experimental content analysis
/// (instrument / texture / mood tags) on top of the basic genre descriptors.</summary>
public sealed record DetailedFeatures(
    TrackFeatures Base,
    double SubBassRatio,
    double BassRatio,
    double MidRatio,
    double HighRatio,
    double SpectralTilt,
    double Percussiveness,
    double VocalLikelihood)
{
    public double Flatness => Base.SpectralFlatness;
}
