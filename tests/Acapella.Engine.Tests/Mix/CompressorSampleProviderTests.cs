using Acapella.Engine.Mix;
using NAudio.Wave;

namespace Acapella.Engine.Tests.Mix;

public class CompressorSampleProviderTests
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
    public void Read_SignalAboveThreshold_ReducesPeakBelowUncompressed()
    {
        int sampleRate = 44100;
        // 0dBFS sine sits well above a -18dB threshold, so a 4:1 ratio should visibly reduce peak.
        var samples = GenerateSineWave(440, sampleRate, sampleRate, amplitude: 1.0f);

        var source = new ArraySampleProvider(samples, sampleRate);
        var compressor = new CompressorSampleProvider(source, thresholdDb: -18f, ratio: 4f);
        var output = ReadAll(compressor, sampleRate);

        // Skip the attack ramp-up region; check steady-state peak.
        float steadyPeak = output.Skip(sampleRate / 2).Max(Math.Abs);
        Assert.True(steadyPeak < 0.95f, $"Expected compressed peak below input peak (1.0), got {steadyPeak}.");
    }

    [Fact]
    public void Read_SignalBelowThreshold_IsUnaffected()
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(440, sampleRate, sampleRate, amplitude: 0.05f);

        var source = new ArraySampleProvider(samples, sampleRate);
        var compressor = new CompressorSampleProvider(source, thresholdDb: -18f, ratio: 4f);
        var output = ReadAll(compressor, sampleRate);

        float steadyPeak = output.Skip(sampleRate / 2).Max(Math.Abs);
        float inputPeak = samples.Max(Math.Abs);
        Assert.True(Math.Abs(steadyPeak - inputPeak) < 0.01f, $"Expected below-threshold signal roughly unchanged, input {inputPeak}, output {steadyPeak}.");
    }
}
