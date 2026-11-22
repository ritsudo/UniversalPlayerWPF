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
        try
        {
            return new MediaFoundationReader(path);
        }
        catch (Exception first)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var reader = new StreamMediaFoundationReader(stream);
                return new OwningWaveStream(stream, reader);
            }
            catch
            {
                // fall through to NAudio's native readers (wav / aiff / ...)
            }

            try
            {
                return new AudioFileReader(path);
            }
            catch
            {
                throw first;
            }
        }
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
