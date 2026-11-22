using UniversalPlayer.Classification.Local;

namespace UniversalPlayer.Classification.Content;

public sealed class ContentAnalysisResult
{
    /// <summary>';'-separated tags, strongest first.</summary>
    public string Tags { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public IReadOnlyList<(string Tag, double Score)> Scored { get; init; } = Array.Empty<(string, double)>();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Tags);

    public string[] Split() => Tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The experimental "what is actually in this recording" analyser: it turns the audio
/// descriptors into short descriptive tags - instrument, texture and mood - e.g.
/// "piano;calm;instrumental".
///
/// Features are z-scored against the collection's own statistics (learned by
/// <see cref="Calibrate"/> and stored in <c>models/content_model.json</c>), so a tag only
/// fires when the track is genuinely unusual for this library. Weights are editable there.
/// </summary>
public sealed class ContentAnalyzer
{
    private readonly ContentModelStore _store;
    private ContentModel _model;

    public ContentAnalyzer(ContentModelStore? store = null)
    {
        _store = store ?? new ContentModelStore();
        _model = _store.Load();
    }

    public string ModelPath => _store.FilePath;
    public int TagCount => _model.Tags.Count;
    public bool NeedsCalibration => _model.Means.Count == 0;

    public void Reload() => _model = _store.Load();

    /// <summary>
    /// Learns the per-feature mean/standard deviation from a sample of the collection.
    /// Cheap and only needed once per library (re-run when the library changes a lot).
    /// </summary>
    public bool Calibrate(IReadOnlyCollection<string> paths, int sampleSize, CancellationToken ct = default)
    {
        if (paths.Count == 0) return false;

        var sample = paths.Count <= sampleSize
            ? paths.ToList()
            : Enumerable.Range(0, sampleSize).Select(i => paths.ElementAt(i * paths.Count / sampleSize)).ToList();

        var vectors = new List<Dictionary<string, double>>();
        foreach (var path in sample)
        {
            ct.ThrowIfCancellationRequested();
            var features = AudioFeatureExtractor.ExtractDetailed(path, 20, ct);
            if (features is not null)
                vectors.Add(RawVector(features));
        }

        if (vectors.Count < 8)
            return false;

        foreach (var name in vectors[0].Keys)
        {
            var values = vectors.Where(v => v.ContainsKey(name)).Select(v => v[name]).ToArray();
            if (values.Length == 0) continue;
            var mean = values.Average();
            var variance = values.Select(v => (v - mean) * (v - mean)).Average();
            var std = Math.Sqrt(variance);
            _model.Means[name] = mean;
            _model.Stds[name] = Math.Max(std, Math.Max(1e-3, Math.Abs(mean) * 0.05));
        }

        _store.Save(_model);
        return true;
    }

    public ContentAnalysisResult? Analyze(string path, CancellationToken ct = default)
    {
        var detailed = AudioFeatureExtractor.ExtractDetailed(path, 40, ct);
        return detailed is null ? null : Analyze(detailed);
    }

    public ContentAnalysisResult Analyze(DetailedFeatures f)
    {
        var features = ZScores(RawVector(f));
        var scored = new List<(string Tag, double Score)>(_model.Tags.Count);

        foreach (var rule in _model.Tags)
        {
            var z = rule.Bias;
            foreach (var (feature, weight) in rule.Weights)
                if (features.TryGetValue(feature, out var value))
                    z += weight * value;
            scored.Add((rule.Name, Sigmoid(z)));
        }

        var ordered = scored.OrderByDescending(s => s.Score).ToList();
        var picked = ordered.Where(s => s.Score >= _model.Threshold).Take(_model.MaxTags).ToList();
        if (picked.Count == 0)
            picked = ordered.Take(2).ToList(); // always give the strongest guesses

        var tags = string.Join(";", picked.Select(p => p.Tag));
        var confidence = picked.Count == 0 ? 0 : picked.Average(p => p.Score);
        return new ContentAnalysisResult { Tags = tags, Confidence = confidence, Scored = picked };
    }

    private Dictionary<string, double> ZScores(Dictionary<string, double> raw)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in raw)
        {
            if (_model.Means.TryGetValue(name, out var mean) && _model.Stds.TryGetValue(name, out var std) && std > 1e-9)
                result[name] = Math.Clamp((value - mean) / std / 2.0, -1, 1);
            else
                result[name] = 0;
        }
        return result;
    }

    /// <summary>The raw (unclamped) descriptors the model z-scores.</summary>
    public static Dictionary<string, double> RawVector(DetailedFeatures f)
    {
        var b = f.Base;
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["tempo"] = b.Tempo,
            ["rms"] = b.Rms,
            ["crest"] = b.Crest,
            ["zcr"] = b.ZeroCrossingRate,
            ["centroid"] = b.SpectralCentroid,
            ["rolloff"] = b.SpectralRolloff,
            ["flux"] = b.SpectralFlux,
            ["low"] = b.LowEnergyRatio,
            ["onset"] = b.OnsetRate,
            ["dyn"] = b.DynamicRangeDb,
            ["tonality"] = 1 - b.SpectralFlatness,
            ["flat"] = b.SpectralFlatness,
            ["silence"] = b.SilenceRatio,
            ["subBass"] = f.SubBassRatio,
            ["bass"] = f.BassRatio,
            ["mid"] = f.MidRatio,
            ["high"] = f.HighRatio,
            ["tilt"] = f.SpectralTilt,
            ["perc"] = f.Percussiveness,
            ["vocal"] = f.VocalLikelihood
        };
    }

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));
}
