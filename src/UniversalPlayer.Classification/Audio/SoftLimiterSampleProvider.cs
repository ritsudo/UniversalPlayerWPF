using NAudio.Wave;

namespace UniversalPlayer.Classification.Audio;

/// <summary>
/// A transparent soft limiter: samples below the knee pass through untouched, anything above
/// is bent smoothly towards full scale so a normalisation boost can never clip at the device.
/// Memoryless, so it adds no latency.
/// </summary>
public sealed class SoftLimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _knee;

    public SoftLimiterSampleProvider(ISampleProvider source, float knee = 0.75f)
    {
        _source = source;
        _knee = Math.Clamp(knee, 0.5f, 0.95f);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        for (var i = 0; i < read; i++)
        {
            var sample = buffer[offset + i];
            var magnitude = Math.Abs(sample);
            if (magnitude <= _knee) continue;

            var over = (magnitude - _knee) / (1f - _knee);
            var limited = _knee + (1f - _knee) * (float)Math.Tanh(over);
            buffer[offset + i] = sample < 0 ? -limited : limited;
        }
        return read;
    }
}
