using NAudio.Wave;

namespace Acapella.Engine.Preview;

/// <summary>
/// Wraps a sample provider and counts samples actually pulled through it, exposing that as an
/// elapsed-time position. When the wrapped stream is fed to a real-time consumer (WASAPI, or a
/// test sink that simulates real-time pulling), this position absorbs whatever start/buffer
/// latency and pacing jitter that consumer has -- it reflects genuine playback progress rather
/// than an independent wall-clock guess (audit A2: the previous Stopwatch-based clock could drift
/// arbitrarily far from real audio position).
/// </summary>
public class PositionTrackingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private long _samplesRead;

    public PositionTrackingSampleProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double PositionMs
    {
        get
        {
            long samples = Interlocked.Read(ref _samplesRead);
            return samples / (double)WaveFormat.Channels / WaveFormat.SampleRate * 1000.0;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        Interlocked.Add(ref _samplesRead, read);
        return read;
    }
}
