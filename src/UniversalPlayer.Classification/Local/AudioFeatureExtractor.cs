using NAudio.Dsp;
using NAudio.Wave;
using UniversalPlayer.Classification.Audio;

namespace UniversalPlayer.Classification.Local;

/// <summary>
/// Decodes the first N seconds of a file through Windows Media Foundation (so the audio
/// track of an .mp4 is used, exactly like a music file) and derives the spectral / rhythmic
/// descriptors consumed by <see cref="LocalGenreModel"/>.
/// </summary>
public static class AudioFeatureExtractor
{
    private const int FrameSize = 1024;
    private const int HopSize = 512;
    private const int M = 10; // 2^10 == FrameSize

    public static TrackFeatures? Extract(string path, double maxSeconds = 30, CancellationToken ct = default)
    {
        float[] samples;
        int sampleRate;
        try
        {
            (samples, sampleRate) = ReadMono(path, maxSeconds, ct);
        }
        catch
        {
            return null;
        }

        if (samples.Length < FrameSize * 2 || sampleRate <= 0)
            return null;

        return Analyse(samples, sampleRate);
    }

    private static (float[] Samples, int SampleRate) ReadMono(string path, double maxSeconds, CancellationToken ct)
    {
        using var reader = AudioStreamFactory.Open(path);
        var provider = reader.ToSampleProvider();
        var channels = Math.Max(1, provider.WaveFormat.Channels);
        var rate = provider.WaveFormat.SampleRate;
        var maxMono = (int)Math.Min(rate * maxSeconds, 60 * rate * 4);
        var mono = new List<float>(Math.Min(maxMono, rate * 30));
        var buffer = new float[channels * 4096];

        while (mono.Count < maxMono)
        {
            ct.ThrowIfCancellationRequested();
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            for (var i = 0; i + channels <= read; i += channels)
            {
                float sum = 0f;
                for (var c = 0; c < channels; c++)
                    sum += buffer[i + c];
                mono.Add(sum / channels);
                if (mono.Count >= maxMono) break;
            }
        }

        return (mono.ToArray(), rate);
    }

    private static TrackFeatures Analyse(float[] samples, int sampleRate)
    {
        var window = new double[FrameSize];
        for (var i = 0; i < FrameSize; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));

        double nyquist = sampleRate / 2.0;
        double binHz = sampleRate / (double)FrameSize;

        var fft = new Complex[FrameSize];
        var prevMag = new double[FrameSize / 2];
        var flux = new List<double>();
        var rmsList = new List<double>();
        var zcrList = new List<double>();
        var centroidList = new List<double>();
        var rolloffList = new List<double>();
        var flatnessList = new List<double>();
        var lowRatioList = new List<double>();

        double peak = 0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs(s));

        var frameCount = (samples.Length - FrameSize) / HopSize + 1;
        for (var f = 0; f < frameCount; f++)
        {
            var offset = f * HopSize;

            double energy = 0, zc = 0;
            var prev = samples[offset];
            for (var i = 0; i < FrameSize; i++)
            {
                var s = samples[offset + i];
                energy += s * s;
                if ((s >= 0 && prev < 0) || (s < 0 && prev >= 0)) zc++;
                prev = s;
                fft[i].X = (float)(s * window[i]);
                fft[i].Y = 0;
            }

            var rms = Math.Sqrt(energy / FrameSize);
            var mag = new double[FrameSize / 2];
            FastFourierTransform.FFT(true, M, fft);

            double magSum = 0, magLogSum = 0, centroidNum = 0, lowEnergy = 0;
            for (var k = 0; k < FrameSize / 2; k++)
            {
                var m = Math.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);
                mag[k] = m;
                magSum += m;
                magLogSum += Math.Log(m + 1e-9);
                centroidNum += k * binHz * m;
                if (k * binHz < 250) lowEnergy += m * m;
            }

            var meanMag = magSum / (FrameSize / 2) + 1e-9;
            var geoMag = Math.Exp(magLogSum / (FrameSize / 2));
            var flatness = Math.Clamp(geoMag / meanMag, 0, 1);

            var centroid = magSum > 1e-9 ? centroidNum / magSum : 0;

            double cumulative = 0, rolloff = 0;
            var target = magSum * 0.85;
            for (var k = 0; k < FrameSize / 2; k++)
            {
                cumulative += mag[k];
                if (cumulative >= target) { rolloff = k * binHz; break; }
            }

            double fluxFrame = 0, totalEnergy = 0;
            for (var k = 0; k < FrameSize / 2; k++)
            {
                var d = mag[k] - prevMag[k];
                if (d > 0) fluxFrame += d;
                prevMag[k] = mag[k];
                totalEnergy += mag[k] * mag[k];
            }

            rmsList.Add(rms);
            zcrList.Add(zc / (double)FrameSize);
            centroidList.Add(centroid);
            rolloffList.Add(rolloff);
            flatnessList.Add(flatness);
            lowRatioList.Add(totalEnergy > 1e-9 ? lowEnergy / totalEnergy : 0);
            flux.Add(fluxFrame / (magSum + 1e-9));
        }

        var fps = sampleRate / (double)HopSize;
        var tempo = EstimateTempo(flux, fps);
        var onsetRate = EstimateOnsetRate(flux, fps);
        var dynRange = DynamicRangeDb(rmsList);
        var silence = rmsList.Count > 0 ? rmsList.Count(r => r < 0.004) / (double)rmsList.Count : 0;

        return new TrackFeatures(
            DurationSeconds: samples.Length / (double)sampleRate,
            Rms: Mean(rmsList),
            Peak: peak,
            Crest: Mean(rmsList) > 1e-9 ? peak / Mean(rmsList) : 0,
            ZeroCrossingRate: Mean(zcrList),
            SpectralCentroid: Mean(centroidList),
            SpectralRolloff: Mean(rolloffList),
            SpectralFlatness: Mean(flatnessList),
            SpectralFlux: Mean(flux),
            LowEnergyRatio: Mean(lowRatioList),
            Tempo: tempo,
            OnsetRate: onsetRate,
            DynamicRangeDb: dynRange,
            SilenceRatio: silence);
    }

    private static double EstimateTempo(List<double> flux, double fps)
    {
        if (flux.Count < 4) return 0;
        var minLag = Math.Max(1, (int)(fps * 0.3));
        var maxLag = Math.Min(flux.Count - 1, (int)(fps * 1.0));
        double best = 0;
        var bestLag = 0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < flux.Count; i++)
                sum += flux[i] * flux[i + lag];
            if (sum > best) { best = sum; bestLag = lag; }
        }
        if (bestLag == 0) return 0;
        var tempo = 60.0 * fps / bestLag;
        while (tempo < 55 && tempo > 0) tempo *= 2;
        while (tempo > 200) tempo /= 2;
        return tempo;
    }

    private static double EstimateOnsetRate(List<double> flux, double fps)
    {
        if (flux.Count < 3) return 0;
        var mean = Mean(flux);
        var std = Math.Sqrt(flux.Sum(v => (v - mean) * (v - mean)) / flux.Count) + 1e-9;
        var peaks = flux.Count(v => v > mean + std);
        var seconds = flux.Count / fps;
        return seconds > 0 ? peaks / seconds : 0;
    }

    private static double DynamicRangeDb(List<double> rms)
    {
        if (rms.Count < 4) return 0;
        var sorted = rms.Where(v => v > 1e-6).OrderBy(v => v).ToArray();
        if (sorted.Length < 4) return 0;
        var low = sorted[(int)(sorted.Length * 0.10)];
        var high = sorted[(int)(sorted.Length * 0.95)];
        return 20 * Math.Log10((high + 1e-9) / (low + 1e-9));
    }

    private static double Mean(List<double> values) => values.Count == 0 ? 0 : values.Average();
}
