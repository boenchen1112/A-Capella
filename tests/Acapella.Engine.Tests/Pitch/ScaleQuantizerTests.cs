using Acapella.Engine.Pitch;

namespace Acapella.Engine.Tests.Pitch;

public class ScaleQuantizerTests
{
    [Theory]
    [InlineData(430.0, 440.0)]   // flat A4 -> snaps up to A4
    [InlineData(450.0, 440.0)]   // sharp A4 -> snaps down to A4
    [InlineData(220.0, 220.0)]   // exact A3 -> stays put
    [InlineData(261.0, 261.6256)] // near C4 -> snaps to C4
    public void NearestNoteFrequency_SnapsToClosestSemitone(double input, double expected)
    {
        double result = ScaleQuantizer.NearestNoteFrequency(input);
        Assert.InRange(result, expected * 0.999, expected * 1.001);
    }
}
