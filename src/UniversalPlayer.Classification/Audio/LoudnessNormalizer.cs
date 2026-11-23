using UniversalPlayer.Classification.Local;

namespace UniversalPlayer.Classification.Audio;

/// <summary>
/// Works out a playback gain that lifts quiet recordings to a comfortable level while never
/// clipping the loud ones.
///
/// Quiet tracks are amplified (up to +12 dB); tracks that are already at a normal level are
/// left untouched, so switching "normalize" on never makes the library quieter - it only fixes
/// the tracks that are too low. The gain is limited by the available headroom, so a very
/// dynamic recording with near full-scale peaks is boosted only a little.
/// </summary>
public static class LoudnessNormalizer
{
    /// <summary>Reference loudness, roughly -17 dBFS - about streaming-platform level.</summary>
    public const double TargetRms = 0.14;

    public const double MaxGain = 4.0;   // +12 dB
    public const double MinGain = 1.0;   // never attenuate: loud tracks stay as they are

    /// <summary>Peak headroom kept aside when boosting (about -2.5 dB), so a peak the
    /// measurement window missed cannot clip the output.</summary>
    private const double PeakCeiling = 0.75;

    public static double Gain(double rms, double peak)
    {
        if (rms <= 1e-6) return 1.0;

        var byLoudness = TargetRms / rms;
        var byHeadroom = PeakCeiling / Math.Max(peak, 1e-6);
        var gain = Math.Min(byLoudness, byHeadroom);

        return Math.Clamp(gain, MinGain, MaxGain);
    }

    public static double Gain(TrackFeatures features) => Gain(features.Rms, features.Peak);

    public static double Gain(DetailedFeatures features) => Gain(features.Base);
}
