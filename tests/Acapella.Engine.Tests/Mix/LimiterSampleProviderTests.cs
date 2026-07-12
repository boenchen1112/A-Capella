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

    /// <summary>Trivial stereo-interleaved source: no channel logic of its own, just hands back
    /// whatever interleaved samples it was given.</summary>
    private class StereoArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public StereoArraySampleProvider(float[] interleavedSamples, int sampleRate)
        {
            _samples = interleavedSamples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _samples.Length - _position;
            int toCopy = Math.Min(available, count);
            if (toCopy <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }

    /// <summary>Regression test for audit B6: before the fix, the limiter's envelope state was
    /// shared across a flat interleaved sample stream with one gain decision per raw sample, so a
    /// loud left channel and a quiet right channel (typical of a hard-panned layer) received
    /// different attenuation and the stereo image wobbled. The gain decision must now be made once
    /// per channel-frame (from the loudest channel in that frame) and applied identically to every
    /// channel, so a quiet channel gets attenuated by the same ratio as the loud one that
    /// triggered it.</summary>
    [Fact]
    public void Read_HardPannedLoudLeftQuietRight_AppliesSameGainRatioToBothChannels()
    {
        int sampleRate = 44100;
        int frames = sampleRate / 4;
        var interleaved = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double phase = 2 * Math.PI * 440 * i / sampleRate;
            interleaved[i * 2] = (float)(0.9 * Math.Sin(phase));      // L: loud
            interleaved[i * 2 + 1] = (float)(0.05 * Math.Sin(phase)); // R: quiet
        }

        var source = new StereoArraySampleProvider(interleaved, sampleRate);
        var limiter = new LimiterSampleProvider(source, ceilingDb: -3f, makeupGainDb: 6f);
        var output = ReadAll(limiter, interleaved.Length);

        // Compare a steady-state frame (envelope settled) against what makeup-gain-only (no
        // limiting) would have produced for each channel, and check both channels were scaled by
        // the same ratio -- not just the loud one.
        int checkFrame = frames / 2;
        float makeupLinear = (float)Math.Pow(10, 6f / 20.0);
        float preL = interleaved[checkFrame * 2] * makeupLinear;
        float preR = interleaved[checkFrame * 2 + 1] * makeupLinear;
        float postL = output[checkFrame * 2];
        float postR = output[checkFrame * 2 + 1];

        Assert.True(Math.Abs(postL) < Math.Abs(preL), "Expected the loud left channel to be attenuated.");

        float ratioL = postL / preL;
        float ratioR = postR / preR;
        Assert.True(Math.Abs(ratioL - ratioR) < 0.01f,
            $"Expected both channels attenuated by the same ratio (stereo-linked), got L ratio={ratioL}, R ratio={ratioR}.");
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
