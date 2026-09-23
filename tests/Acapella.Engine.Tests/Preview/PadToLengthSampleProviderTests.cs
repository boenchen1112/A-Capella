using Acapella.Engine.Mix;
using Acapella.Engine.Preview;
using NAudio.Wave.SampleProviders;

namespace Acapella.Engine.Tests.Preview;

public class PadToLengthSampleProviderTests
{
    private static long PullAll(PadToLengthSampleProvider p, out bool allPaddingSilent, int sourceSamples)
    {
        var buf = new float[1000];
        long total = 0;
        allPaddingSilent = true;
        int n;
        while ((n = p.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
                if (total + i >= sourceSamples && buf[i] != 0f) allPaddingSilent = false;
            total += n;
        }
        return total;
    }

    [Fact]
    public void ShortSource_IsPaddedWithSilenceToAtLeastLength_ThenEnds()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Enumerable.Repeat(0.5f, 441).ToArray(), 44100)); // 10ms
        var padded = new PadToLengthSampleProvider(source, lengthMs: 100);

        long total = PullAll(padded, out bool silent, sourceSamples: 882);

        Assert.True(total >= 4410 * 2, $"Expected >= 100ms of stereo samples, got {total}.");
        Assert.True(total <= (4410 + 2) * 2);
        Assert.True(silent);
        Assert.True(padded.Ended);
    }

    [Fact]
    public void EmptySource_IsPaddedToLength()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Array.Empty<float>(), 44100));
        var padded = new PadToLengthSampleProvider(source, lengthMs: 50);
        Assert.True(PullAll(padded, out _, 0) >= 2205 * 2);
    }

    [Fact]
    public void LongerSource_IsPassedThroughUntruncated()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Enumerable.Repeat(0.5f, 4410).ToArray(), 44100)); // 100ms
        var padded = new PadToLengthSampleProvider(source, lengthMs: 10);
        Assert.Equal(4410 * 2, PullAll(padded, out _, 8820));
    }

    [Fact]
    public void ZeroOrNegativeLength_EndsWhenSourceEnds()
    {
        var source = new MonoToStereoSampleProvider(new ArraySampleProvider(Array.Empty<float>(), 44100));
        var padded = new PadToLengthSampleProvider(source, lengthMs: -5);
        Assert.Equal(0, padded.Read(new float[100], 0, 100));
        Assert.True(padded.Ended);
    }
}
