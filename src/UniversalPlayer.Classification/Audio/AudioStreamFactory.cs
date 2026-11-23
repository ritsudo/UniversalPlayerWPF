using NAudio.Wave;

namespace UniversalPlayer.Classification.Audio;

/// <summary>
/// Opens any supported audio file (mp3, mp4/m4a, flac, wav, ...) as a decodable stream
/// through Windows Media Foundation - so the audio track of an .mp4 is read exactly like an
/// .mp3. Falls back to a stream-backed reader and finally to NAudio's own readers.
/// </summary>
public static class AudioStreamFactory
{
    public static WaveStream Open(string path)
    {
        // 1. Media Foundation by path (mp3, m4a, mp4/aac, flac, wav, ...)
        try
        {
            return new MediaFoundationReader(path);
        }
        catch
        {
            // try the next decoder
        }

        // 2. Media Foundation over a stream (helps some containers/paths)
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                return new OwningWaveStream(stream, new StreamMediaFoundationReader(stream));
            }
            catch
            {
                stream.Dispose();
            }
        }
        catch
        {
            // try the next decoder
        }

        // 3. NAudio's own readers (wav / aiff / ...)
        try
        {
            return new AudioFileReader(path);
        }
        catch
        {
            // try the next decoder
        }

        // 4. ffmpeg (Opus/AV1 mp4, APE, damaged files, ...) - shipped in the program folder
        if (FfmpegLocator.IsAvailable)
            return new FfmpegAudioStream(path);

        throw new InvalidOperationException(
            $"No decoder available for '{Path.GetFileName(path)}'. Put ffmpeg.exe next to the player for exotic formats.");
    }

    /// <summary>Keeps the backing stream alive for as long as the reader is used.</summary>
    private sealed class OwningWaveStream : WaveStream
    {
        private readonly Stream _stream;
        private readonly WaveStream _inner;

        public OwningWaveStream(Stream stream, WaveStream inner)
        {
            _stream = stream;
            _inner = inner;
        }

        public override WaveFormat WaveFormat => _inner.WaveFormat;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { /* ignore */ }
                try { _stream.Dispose(); } catch { /* ignore */ }
            }
            base.Dispose(disposing);
        }
    }
}
