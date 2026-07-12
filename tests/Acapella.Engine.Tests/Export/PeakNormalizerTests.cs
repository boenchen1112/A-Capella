using Acapella.Engine.Export;

namespace Acapella.Engine.Tests.Export;

public class PeakNormalizerTests
{
    [Fact]
    public void NormalizeIfClipping_PeakAboveOne_ScalesDownToExactlyOne()
    {
        var samples = new float[] { 0.5f, -2.0f, 1.5f, -0.25f };

        PeakNormalizer.NormalizeIfClipping(samples);

        float peak = samples.Max(Math.Abs);
        Assert.Equal(1.0f, peak, 4);
    }

    [Fact]
    public void NormalizeIfClipping_AlreadySafe_LeavesSamplesUnchanged()
    {
        var samples = new float[] { 0.5f, -0.8f, 0.3f };
        var original = (float[])samples.Clone();

        PeakNormalizer.NormalizeIfClipping(samples);

        Assert.Equal(original, samples);
    }

    [Fact]
    public void NormalizeIfClipping_EmptyArray_DoesNotThrow()
    {
        var samples = Array.Empty<float>();
        var exception = Record.Exception(() => PeakNormalizer.NormalizeIfClipping(samples));
        Assert.Null(exception);
    }
}
