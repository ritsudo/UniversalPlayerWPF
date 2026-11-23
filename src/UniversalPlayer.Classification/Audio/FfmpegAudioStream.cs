using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;

namespace UniversalPlayer.Classification.Audio;

/// <summary>
/// Last-resort decoder: pipes PCM out of ffmpeg.exe, so anything ffmpeg understands plays -
/// Opus/AV1 in .mp4, Monkey's Audio (.ape), WMA variants, damaged files, ... Used only when
/// Windows Media Foundation cannot open the file.
///
/// Seeking restarts ffmpeg at the requested position (-ss), which is quick enough for the
/// player's slider.
/// </summary>
public sealed class FfmpegAudioStream : WaveStream
{
    private const int OutputSampleRate = 44100;
    private const int OutputChannels = 2;
    private const int ReadTimeoutMs = 10000;
    private static readonly WaveFormat OutputFormat = new(OutputSampleRate, 16, OutputChannels);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly TimeSpan _duration;

    private Process? _process;
    private Stream? _output;
    private long _offsetBytes;      // bytes at the position ffmpeg was started from
    private long _streamedBytes;    // bytes produced since that position
    private bool _disposed;

    public FfmpegAudioStream(string path)
    {
        var exe = FfmpegLocator.ExecutablePath
                  ?? throw new InvalidOperationException("ffmpeg.exe was not found next to the player or on the PATH.");

        _path = path;
        _duration = FfmpegLocator.ProbeDuration(path);
        StartAt(exe, TimeSpan.Zero);
    }

    public override WaveFormat WaveFormat => OutputFormat;

    public override long Length
    {
        get
        {
            if (_duration <= TimeSpan.Zero) return 0;
            return (long)(_duration.TotalSeconds * OutputFormat.AverageBytesPerSecond);
        }
    }

    public override long Position
    {
        get
        {
            lock (_gate)
                return _offsetBytes + _streamedBytes;
        }
        set
        {
            lock (_gate)
            {
                if (_disposed) return;
                var targetBytes = Math.Max(0, value);
                var deltaBytes = Math.Abs(targetBytes - (_offsetBytes + _streamedBytes));
                if (deltaBytes < OutputFormat.AverageBytesPerSecond) // < 1 second: not worth a restart
                    return;

                var exe = FfmpegLocator.ExecutablePath;
                if (exe is null) return;
                RestartAt(exe, TimeSpan.FromSeconds(targetBytes / (double)OutputFormat.AverageBytesPerSecond));
            }
        }
    }

    /// <summary>
    /// Fills the whole buffer before returning. A pipe hands back whatever happens to be
    /// available (ffmpeg writes in ~20 ms chunks), and NAudio queues the entire buffer to the
    /// sound card regardless of how much was filled - so a short read would leave stale audio
    /// in the tail and produce garbled sound.
    ///
    /// The wait is bounded: if the decoder stalls the process is dropped instead of blocking
    /// the playback or analysis thread forever.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_disposed || _output is null) return 0;

            var total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    var pending = _output.ReadAsync(buffer, offset + total, count - total);
                    if (!pending.Wait(ReadTimeoutMs))
                    {
                        Stop();          // stalled decoder: treat as end of stream
                        break;
                    }
                    read = pending.GetAwaiter().GetResult();
                }
                catch
                {
                    break;
                }

                if (read <= 0) break;   // end of stream / ffmpeg finished
                total += read;
            }

            _streamedBytes += total;
            return total;
        }
    }

    private void RestartAt(string exe, TimeSpan from)
    {
        Stop();
        StartAt(exe, from);
    }

    private void StartAt(string exe, TimeSpan from)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-nostdin");
        if (from > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(from.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(_path);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a:0");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(OutputSampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(OutputChannels.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("pipe:1");

        var errorText = new StringBuilder();
        var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errorText)
            {
                if (errorText.Length < 4000) errorText.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        _offsetBytes = (long)(from.TotalSeconds * OutputFormat.AverageBytesPerSecond);
        _streamedBytes = 0;
        _process = process;
        _output = process.StandardOutput.BaseStream;

        // Give ffmpeg a moment so an immediate failure is reported instead of a silent end of stream.
        if (process.WaitForExit(400))
        {
            var message = errorText.ToString().Trim();
            Stop();
            throw new InvalidOperationException($"ffmpeg could not decode '{Path.GetFileName(_path)}'" +
                                                (message.Length > 0 ? ": " + message : "."));
        }
    }

    private void Stop()
    {
        if (_output is not null)
        {
            try { _output.Dispose(); } catch { /* ignore */ }
            _output = null;
        }
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(true);
            }
            catch { /* ignore */ }
            try { _process.Dispose(); } catch { /* ignore */ }
            _process = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                Stop();
            }
        }
        base.Dispose(disposing);
    }
}
