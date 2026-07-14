using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

public class MeterTapSampleProviderTests
{
    private const int SampleRate = 44100;

    private static float[] GenerateFullScaleSine(int count, double freqHz)
    {
        var samples = new float[count];
        for (int i = 0; i < count; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * freqHz * i / SampleRate);
        return samples;
    }

    [Fact]
    public void Read_FullScaleSine_ReportsPeakWithinHalfDbOf0Dbfs()
    {
        var sine = GenerateFullScaleSine(SampleRate, freqHz: 440);
        var tap = new MeterTapSampleProvider(new ArraySampleProvider(sine, SampleRate));

        var buffer = new float[2048];
        int totalRead = 0;
        while (totalRead < sine.Length)
        {
            int read = tap.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            totalRead += read;
        }

        // A full-cycle block always contains a sample within a fraction of a dB of the true peak
        // (1.0) as long as the block is long enough to cover several periods -- true here (2048
        // samples at 440Hz spans ~20 cycles).
        Assert.InRange(tap.PeakDb, -0.5f, 0.5f);
    }

    [Fact]
    public void Read_FullScaleSine_RmsIsRoughlyPeakMinus3Db()
    {
        var sine = GenerateFullScaleSine(4096, freqHz: 440);
        var tap = new MeterTapSampleProvider(new ArraySampleProvider(sine, SampleRate));

        var buffer = new float[4096];
        tap.Read(buffer, 0, buffer.Length);

        // RMS of a full-scale sine is amplitude/sqrt(2), i.e. ~-3.01dB relative to peak.
        Assert.InRange(tap.RmsDb - tap.PeakDb, -3.5f, -2.5f);
    }

    [Fact]
    public void Read_Silence_ReportsNegativeInfinityDb()
    {
        var silence = new float[1024];
        var tap = new MeterTapSampleProvider(new ArraySampleProvider(silence, SampleRate));

        var buffer = new float[1024];
        tap.Read(buffer, 0, buffer.Length);

        Assert.True(float.IsNegativeInfinity(tap.PeakDb));
        Assert.True(float.IsNegativeInfinity(tap.RmsDb));
    }

    [Fact]
    public void Read_PassesAudioThroughUnmodified()
    {
        var sine = GenerateFullScaleSine(512, freqHz: 440);
        var tap = new MeterTapSampleProvider(new ArraySampleProvider((float[])sine.Clone(), SampleRate));

        var buffer = new float[512];
        int read = tap.Read(buffer, 0, buffer.Length);

        Assert.Equal(512, read);
        for (int i = 0; i < 512; i++)
            Assert.Equal(sine[i], buffer[i], precision: 6);
    }
}
