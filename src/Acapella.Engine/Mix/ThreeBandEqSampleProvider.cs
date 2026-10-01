using NAudio.Dsp;
using NAudio.Wave;

namespace Acapella.Engine.Mix;

/// <summary>3-band shelf/bell EQ (low shelf, mid bell, high shelf) — sufficient for v1 per the build plan.</summary>
public class ThreeBandEqSampleProvider : ISampleProvider
{
    private const float LowShelfFreq = 150f;
    private const float MidBellFreq = 1000f;
    private const float HighShelfFreq = 6000f;

    private readonly ISampleProvider _source;
    private BiQuadFilter _lowShelf;
    private BiQuadFilter _midBell;
    private BiQuadFilter _highShelf;

    public ThreeBandEqSampleProvider(ISampleProvider source, float lowGainDb, float midGainDb, float highGainDb)
    {
        _source = source;
        int sampleRate = source.WaveFormat.SampleRate;
        _lowShelf = BiQuadFilter.LowShelf(sampleRate, LowShelfFreq, 1f, lowGainDb);
        _midBell = BiQuadFilter.PeakingEQ(sampleRate, MidBellFreq, 1f, midGainDb);
        _highShelf = BiQuadFilter.HighShelf(sampleRate, HighShelfFreq, 1f, highGainDb);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        for (int i = 0; i < read; i++)
        {
            float sample = buffer[offset + i];
            sample = _lowShelf.Transform(sample);
            sample = _midBell.Transform(sample);
            sample = _highShelf.Transform(sample);
            buffer[offset + i] = sample;
        }

        return read;
    }
}
