using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>Feeds a fixed mono float[] buffer as an ISampleProvider; loops silently past the end.</summary>
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
        int written = 0;
        while (written < count)
        {
            if (_position >= _samples.Length)
            {
                buffer[offset + written] = 0f;
            }
            else
            {
                buffer[offset + written] = _samples[_position];
                _position++;
            }
            written++;
        }
        return written;
    }
}
