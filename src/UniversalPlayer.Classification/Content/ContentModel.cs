using System.Text.Json;
using UniversalPlayer.Core.Data;

namespace UniversalPlayer.Classification.Content;

/// <summary>One descriptive tag (e.g. "piano", "calm") as a logistic unit over z-scored audio features.</summary>
public sealed class ContentTagRule
{
    public string Name { get; set; } = string.Empty;
    public double Bias { get; set; }
    public Dictionary<string, double> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The local content-analysis model. It ships as a readable JSON file in the program's
/// <c>models</c> folder so weights can be inspected and tuned without recompiling.
///
/// Features are z-scored against the statistics of the user's own collection
/// (<see cref="Means"/>/<see cref="Stds"/>, filled in by a calibration pass), so a tag fires
/// when a track is clearly above/below what is typical <em>for this library</em>, which keeps
/// the tags selective instead of firing on everything.
/// </summary>
public sealed class ContentModel
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Tags with a sigmoid score above this are kept (the best two are always kept).</summary>
    public double Threshold { get; set; } = 0.55;
    public int MaxTags { get; set; } = 5;

    /// <summary>Per-feature mean, learned from the collection.</summary>
    public Dictionary<string, double> Means { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-feature standard deviation, learned from the collection.</summary>
    public Dictionary<string, double> Stds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feature names the weights refer to (documentation for hand tuning).</summary>
    public List<string> FeatureOrder { get; set; } = new();

    public List<ContentTagRule> Tags { get; set; } = new();

    /// <summary>Built-in model used when no (current) file is present.</summary>
    public static ContentModel CreateDefault()
    {
        var model = new ContentModel
        {
            FeatureOrder = new List<string>
            {
                "tempo", "rms", "crest", "zcr", "centroid", "rolloff", "flux", "low", "onset", "dyn",
                "tonality", "flat", "silence", "subBass", "bass", "mid", "high", "tilt", "perc", "vocal"
            }
        };

        void Tag(string name, double bias, params (string Feature, double Weight)[] weights)
        {
            var rule = new ContentTagRule { Name = name, Bias = bias };
            foreach (var (feature, weight) in weights) rule.Weights[feature] = weight;
            model.Tags.Add(rule);
        }

        Tag("piano", -0.75, ("mid", 1.4), ("tonality", 1.0), ("perc", -1.0), ("subBass", -0.8), ("flat", -0.6), ("dyn", 0.4));
        Tag("acoustic guitar", -0.55, ("mid", 1.4), ("tonality", 1.1), ("high", 0.5), ("subBass", -0.9), ("rms", -0.4), ("perc", 0.3));
        Tag("electric guitar", -0.50, ("mid", 1.2), ("perc", 0.9), ("high", 0.7), ("flat", 0.8), ("flux", 0.5), ("subBass", -0.3));
        Tag("synthesizer", -0.50, ("flat", 1.2), ("high", 0.9), ("subBass", 0.8), ("tonality", -0.6), ("mid", 0.5));
        Tag("strings", -0.70, ("tonality", 1.2), ("mid", 1.0), ("perc", -0.8), ("zcr", -0.5), ("dyn", 0.6));
        Tag("organ", -1.00, ("tonality", 1.2), ("low", 0.6), ("mid", 0.7), ("perc", -0.7), ("flat", -0.6));
        Tag("brass", -0.70, ("mid", 1.1), ("high", 1.1), ("rms", 0.7), ("perc", 0.5), ("tonality", 0.4));
        Tag("drums", -0.60, ("perc", 1.8), ("subBass", 0.9), ("onset", 0.9), ("tonality", -0.7));
        Tag("drum machine", -0.70, ("perc", 1.4), ("subBass", 1.0), ("flat", 0.8), ("onset", 0.7), ("tonality", -0.6));
        Tag("instrumental", -0.50, ("vocal", -1.6));
        Tag("vocals", -0.30, ("vocal", 1.6));
        Tag("solo", -0.40, ("onset", -1.1), ("flux", -0.7), ("perc", -0.6), ("silence", 0.6));
        Tag("calm", -0.50, ("rms", -1.3), ("onset", -1.0), ("tempo", -0.5), ("dyn", 0.4), ("silence", 0.5));
        Tag("energetic", -0.70, ("rms", 1.3), ("onset", 1.1), ("tempo", 0.6), ("perc", 0.6));
        Tag("heavy", -0.60, ("rms", 1.0), ("flat", 0.9), ("perc", 0.7), ("subBass", 0.6), ("tonality", -0.7));
        Tag("classical", -0.60, ("dyn", 1.4), ("tonality", 1.2), ("perc", -0.9), ("subBass", -0.7), ("onset", -0.5));
        Tag("electronic", -0.70, ("flat", 1.1), ("subBass", 1.0), ("high", 0.8), ("onset", 0.5), ("dyn", -0.6));
        Tag("acoustic", -0.70, ("tonality", 1.3), ("flat", -1.1), ("subBass", -0.7), ("perc", 0.3));
        Tag("jazz", -0.80, ("tonality", 1.0), ("dyn", 0.8), ("mid", 0.7), ("onset", 0.5), ("perc", 0.4));
        Tag("folk", -0.80, ("tonality", 1.1), ("mid", 0.9), ("perc", -0.6), ("rms", -0.7), ("dyn", 0.5));
        Tag("bass driven", -0.70, ("subBass", 1.7), ("bass", 1.1), ("perc", 0.4));
        Tag("bright", -0.50, ("high", 1.4), ("tilt", 1.2), ("centroid", 0.7));
        Tag("dark", -0.50, ("subBass", 1.1), ("tonality", -0.7), ("tilt", -0.7), ("high", -0.9));
        Tag("fast", -0.50, ("tempo", 1.6), ("onset", 0.8));
        Tag("slow", -0.50, ("tempo", -1.6), ("onset", -0.5));

        return model;
    }
}

/// <summary>Loads / persists the content model next to the executable.</summary>
public sealed class ContentModelStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public ContentModelStore(string? path = null) => FilePath = path ?? AppPaths.ContentModelPath;

    public ContentModel Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var model = JsonSerializer.Deserialize<ContentModel>(json, Options);
                if (model is not null && model.Tags.Count > 0 && model.Version >= ContentModel.CurrentVersion)
                    return model;
            }
        }
        catch
        {
            // fall through to defaults (and rewrite the file)
        }

        var fallback = ContentModel.CreateDefault();
        // keep any calibration that was already learned
        try
        {
            if (File.Exists(FilePath))
            {
                var previous = JsonSerializer.Deserialize<ContentModel>(File.ReadAllText(FilePath), Options);
                if (previous is not null && previous.Means.Count > 0)
                {
                    fallback.Means = previous.Means;
                    fallback.Stds = previous.Stds;
                }
            }
        }
        catch
        {
            // ignore
        }

        Save(fallback);
        return fallback;
    }

    public void Save(ContentModel model)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(model, Options));
        }
        catch
        {
            // the model file is a convenience; the built-in defaults still work
        }
    }
}
