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

    /// <summary>Regression test for audit B4's confidence gate: a signal that's an exact delayed
    /// copy of the reference must report high confidence (correlation-based offset is trustworthy),
    /// while pure noise uncorrelated with the reference must report low confidence (caller should
    /// fall back to the settings-based offset instead of trusting a spurious lag).</summary>
    [Fact]
    public void FindOffsetSamplesWithConfidence_ExactDelayedCopy_ReportsHighConfidence()
    {
        var reference = GenerateBurst(4000, 1000, 200);
        var signal = GenerateBurst(4000, 1050, 200);

        var (lag, confidence) = CrossCorrelator.FindOffsetSamplesWithConfidence(reference, signal, maxLagSamples: 500);

        Assert.Equal(50, lag);
        Assert.True(confidence > 0.8, $"Expected high confidence for an exact delayed copy, got {confidence}.");
    }

    [Fact]
    public void FindOffsetSamplesWithConfidence_UncorrelatedNoise_ReportsLowConfidence()
    {
        var reference = GenerateBurst(4000, 1000, 200);
        var random = new Random(7);
        var signal = new float[4000];
        for (int i = 0; i < signal.Length; i++)
            signal[i] = (float)(random.NextDouble() * 2 - 1);

        var (_, confidence) = CrossCorrelator.FindOffsetSamplesWithConfidence(reference, signal, maxLagSamples: 500);

        Assert.True(confidence < 0.3, $"Expected low confidence for uncorrelated noise, got {confidence}.");
    }
}
