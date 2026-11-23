using NAudio.Wave;

namespace UniversalPlayer.Classification.Audio;

/// <summary>
/// Measures a track's normalisation gain on demand - used when a track has not been through
/// the content-analysis pass yet.
///
/// Several short windows spread across the track are measured and the loudest/peakiest one
/// wins, so a quiet intro cannot make the player boost a track that later gets loud.
/// </summary>
public sealed class TrackNormalizer
{
    private static readonly double[] Positions = { 0.15, 0.5, 0.8 };
    private const double ChunkSeconds = 5;

    private readonly TimeSpan _budget;

    public TrackNormalizer(TimeSpan? budget = null) => _budget = budget ?? TimeSpan.FromSeconds(3);

    /// <summary>Returns the gain (1.0 when it cannot be measured in time).</summary>
    public double Measure(string path, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_budget);

        try
        {
            using var reader = AudioStreamFactory.Open(path);
            var provider = reader.ToSampleProvider();
            var totalSeconds = reader.TotalTime.TotalSeconds;

            var positions = totalSeconds > 25
                ? Positions.Select(p => TimeSpan.FromSeconds(totalSeconds * p)).ToArray()
                : new[] { TimeSpan.Zero };

            double examinRms = 0, examinPeak = 0;
            for (var i = 0; i < positions.Length; i++)
            {
                timeout.Token.ThrowIfCancellationRequested();

                try { reader.CurrentTime = positions[i]; }
                catch { /* not seekable - measure from wherever we are */ }

                var (rms, peak) = ReadStats(provider, timeout.Token);
                examinRms = Math.Max(examinRms, rms);
                examinPeak = Math.Max(examinPeak, peak);

                // Extra windows only matter when the track would be boosted; when the first
                // window already says "no boost", stopping early keeps scans fast.
                if (i == 0 && LoudnessNormalizer.Gain(examinRms, examinPeak) <= 1.0)
                    break;
            }

            return examinRms <= 0 ? 1.0 : LoudnessNormalizer.Gain(examinRms, examinPeak);
        }
        catch (OperationCanceledException)
        {
            return 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private static (double Rms, double Peak) ReadStats(ISampleProvider provider, CancellationToken ct)
    {
        var channels = Math.Max(1, provider.WaveFormat.Channels);
        var rate = Math.Max(1, provider.WaveFormat.SampleRate);
        var wanted = (int)(rate * ChunkSeconds);
        var buffer = new float[channels * 2048];
        var samples = new List<float>(wanted);
        var peak = 0.0;

        while (samples.Count < wanted)
        {
            ct.ThrowIfCancellationRequested();
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            for (var i = 0; i + channels <= read; i += channels)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += buffer[i + c];
                var mono = sum / channels;
                samples.Add(mono);
                peak = Math.Max(peak, Math.Abs(mono));
            }
        }

        if (samples.Count == 0) return (0, 0);
        double energy = 0;
        foreach (var s in samples) energy += s * s;
        return (Math.Sqrt(energy / samples.Count), peak);
    }
}
