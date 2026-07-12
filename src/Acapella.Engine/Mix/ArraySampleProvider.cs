using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>Feeds a fixed mono float[] buffer as an ISampleProvider, returning 0 once exhausted
/// (standard ISampleProvider end-of-stream signal) rather than padding with infinite silence.</summary>
public class ArraySampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public ArraySampleProvider(float[] samples, int sampleRate)
    {
        _samples = samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int available = _samples.Length - _position;
        if (available <= 0)
            return 0;

        int toCopy = Math.Min(available, count);
        Array.Copy(_samples, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }
}
