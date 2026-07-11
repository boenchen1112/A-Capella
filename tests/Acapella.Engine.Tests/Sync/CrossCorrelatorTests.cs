using Acapella.Engine.Sync;

namespace Acapella.Engine.Tests.Sync;

public class CrossCorrelatorTests
{
    private static float[] GenerateBurst(int totalLength, int burstStart, int burstLength)
    {
        var signal = new float[totalLength];
        for (int i = 0; i < burstLength && burstStart + i < totalLength; i++)
        {
            double t = i / 44100.0;
            signal[burstStart + i] = (float)Math.Sin(2 * Math.PI * 1000 * t);
        }
        return signal;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(-50)]
    [InlineData(300)]
    public void FindOffsetSamples_DetectsKnownDelay(int injectedDelay)
    {
        var reference = GenerateBurst(4000, 1000, 200);
        var signal = GenerateBurst(4000, 1000 + injectedDelay, 200);

        int detected = CrossCorrelator.FindOffsetSamples(reference, signal, maxLagSamples: 500);

        Assert.Equal(injectedDelay, detected);
    }

    [Fact]
    public void FindOffsetSamples_ZeroSignal_ReturnsWithinRange()
    {
        var reference = GenerateBurst(1000, 200, 100);
        var signal = new float[1000];

        int detected = CrossCorrelator.FindOffsetSamples(reference, signal, maxLagSamples: 100);

        Assert.InRange(detected, -100, 100);
    }
}
