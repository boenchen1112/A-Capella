using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>Feeds a fixed interleaved-stereo float[] buffer as an ISampleProvider -- the frozen-layer
/// playback source (see MixEngine's frozen-layer path). Same end-of-stream behavior as
/// ArraySampleProvider (mono), just for 2 channels.</summary>
public class StereoArraySampleProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public StereoArraySampleProvider(float[] samples, int sampleRate)
    {
        _samples = samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
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
