using UniversalPlayer.Core.Models;

namespace UniversalPlayer.Classification.Local;

/// <summary>
/// Lightweight, self-contained genre classifier used when neither the ID3 tags nor the
/// online databases know a track. It is a multinomial (softmax) model over the 12 audio
/// descriptors produced by <see cref="AudioFeatureExtractor"/>; the weights below encode
/// the acoustic signature of each genre family and can be tuned without retraining.
///
/// The model is deliberately "rough" - it exists to give unlabelled files a sensible
/// bucket so the recommendation engine has something to work with.
/// </summary>
public sealed class LocalGenreModel
{
    // Feature order: tempo, rms, crest, zcr, centroid, rolloff, flux, lowRatio,
    //                onsetRate, dynRange, tonality, silence
    private static readonly string[] Classes =
        { "Electronic", "Hip-Hop", "Metal", "Rock", "Pop", "Jazz", "Classical", "Folk" };

    private static readonly double[] Bias =
        { 0.2, 0.1, 0.0, 0.4, 0.6, 0.0, -0.2, -0.1 };

    private static readonly double[][] Weights =
    {
        //            tempo  rms   crest  zcr   cent   roll   flux   low    onset  dyn    tonal  silence
        /* Electronic */ new[] {  1.6,  0.6, -0.4,  0.2,  0.6,  0.8,  1.4,  1.6,   1.8, -1.2,  0.2, -0.8 },
        /* Hip-Hop    */ new[] { -0.6,  0.8,  0.2,  0.8, -0.6, -0.4,  0.8,  1.8,   0.6,  0.4, -0.6, -0.4 },
        /* Metal      */ new[] {  0.9,  1.8,  0.9,  1.4,  1.8,  1.6,  1.6, -0.2,   1.2, -0.8,  0.0, -1.0 },
        /* Rock       */ new[] {  0.7,  1.2,  0.6,  0.9,  1.0,  0.8,  0.9,  0.4,   0.8,  0.5,  0.6, -0.5 },
        /* Pop        */ new[] {  0.6,  0.9, -0.1,  0.6,  1.4,  1.0,  1.0,  0.6,   0.6, -0.6,  1.0, -0.6 },
        /* Jazz       */ new[] { -0.8, -0.4,  0.8,  0.4,  0.2,  0.2,  0.4,  0.8,   0.4,  1.4,  1.2, -0.2 },
        /* Classical  */ new[] { -1.2, -1.2,  1.2, -1.2, -0.6, -0.8, -0.9,  0.2,  -1.0,  1.8,  1.4,  1.2 },
        /* Folk       */ new[] { -0.6, -0.6,  0.8, -0.4, -0.2, -0.4, -0.4, -0.8,  -0.4,  0.9,  1.0,  0.8 }
    };

    public ClassificationResult Predict(string path, CancellationToken ct = default)
    {
        var features = AudioFeatureExtractor.Extract(path, 30, ct);
        return features is null
            ? ClassificationResult.Empty("audio could not be decoded")
            : Predict(features);
    }

    public ClassificationResult Predict(TrackFeatures f)
    {
        var vector = ToVector(f);

        var logits = new double[Classes.Length];
        for (var c = 0; c < Classes.Length; c++)
        {
            var sum = Bias[c];
            for (var i = 0; i < vector.Length; i++)
                sum += Weights[c][i] * vector[i];
            logits[c] = sum;
        }

        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var total = exp.Sum();
        var probs = exp.Select(e => e / total).ToArray();

        var best = 0;
        for (var i = 1; i < probs.Length; i++)
            if (probs[i] > probs[best]) best = i;

        return new ClassificationResult
        {
            Genre = Classes[best],
            Source = GenreSource.LocalModel,
            Confidence = probs[best],
            Detail = $"local model (tempo {f.Tempo:F0} bpm, centroid {f.SpectralCentroid:F0} Hz)"
        };
    }

    private static double[] ToVector(TrackFeatures f) => new[]
    {
        Clamp((f.Tempo - 60) / 140.0),
        Clamp(f.Rms * 6),
        Clamp((f.Crest - 1.5) / 8.0),
        Clamp(f.ZeroCrossingRate / 0.25),
        Clamp(f.SpectralCentroid / 6000.0),
        Clamp(f.SpectralRolloff / 12000.0),
        Clamp(f.SpectralFlux * 25.0),
        Clamp(f.LowEnergyRatio),
        Clamp(f.OnsetRate / 6.0),
        Clamp(f.DynamicRangeDb / 30.0),
        Clamp(f.Tonality),
        Clamp(f.SilenceRatio)
    };

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);
}
