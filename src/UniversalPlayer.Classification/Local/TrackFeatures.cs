namespace UniversalPlayer.Classification.Local;

/// <summary>Compact set of audio descriptors extracted from a track, used by the local model.</summary>
public sealed record TrackFeatures(
    double DurationSeconds,
    double Rms,
    double Peak,
    double Crest,
    double ZeroCrossingRate,
    double SpectralCentroid,
    double SpectralRolloff,
    double SpectralFlatness,
    double SpectralFlux,
    double LowEnergyRatio,
    double Tempo,
    double OnsetRate,
    double DynamicRangeDb,
    double SilenceRatio)
{
    public double Tonality => Math.Clamp(1.0 - SpectralFlatness, 0, 1);
}
