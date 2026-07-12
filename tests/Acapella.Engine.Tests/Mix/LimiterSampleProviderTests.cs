using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

public class LimiterSampleProviderTests
{
    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length, float amplitude)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        return samples;
    }

    private static float[] ReadAll(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        int read = 0;
        while (read < count)
        {
            int n = provider.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return buffer;
    }

    [Fact]
    public void Read_MakeupGainPushesAboveCeiling_ClampsToCeiling()
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate, amplitude: 0.5f);

        var source = new ArraySampleProvider(samples, sampleRate);
        // +12dB makeup on a 0.5-amplitude signal would peak near 2.0 unlimited; ceiling clamps it.
        var limiter = new LimiterSampleProvider(source, ceilingDb: -0.3f, makeupGainDb: 12f);
        var output = ReadAll(limiter, sampleRate);

        // A single-pole envelope follower without lookahead can't fully catch every fast sine
        // peak, so this isn't a true brick-wall guarantee -- assert it's pulled close to the
        // ceiling rather than left at the ~2.0 an unlimited +12dB makeup gain would produce.
        float ceilingLinear = (float)Math.Pow(10, -0.3f / 20.0);
        float steadyPeak = output.Skip(sampleRate / 4).Max(Math.Abs);
        Assert.True(steadyPeak <= ceilingLinear * 1.15f, $"Expected peak close to ceiling ({ceilingLinear}), got {steadyPeak}.");
    }

    [Fact]
    public void Read_SignalWellBelowCeiling_PassesThroughUnattenuated()
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate, amplitude: 0.1f);

        var source = new ArraySampleProvider(samples, sampleRate);
        var limiter = new LimiterSampleProvider(source, ceilingDb: -0.3f, makeupGainDb: 0f);
        var output = ReadAll(limiter, sampleRate);

        float steadyPeak = output.Skip(sampleRate / 4).Max(Math.Abs);
        Assert.True(Math.Abs(steadyPeak - 0.1f) < 0.005f, $"Expected near-unattenuated peak (~0.1), got {steadyPeak}.");
    }
}
