using Acapella.Engine.Pitch;

namespace Acapella.Engine.Tests.Pitch;

public class PitchDetectorTests
{
    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
        return samples;
    }

    [Theory]
    [InlineData(220.0)]
    [InlineData(440.0)]
    [InlineData(880.0)]
    public void DetectPitchHz_FindsKnownFrequency(double frequencyHz)
    {
        int sampleRate = 44100;
        var samples = GenerateSineWave(frequencyHz, sampleRate, 4096);

        double? detected = PitchDetector.DetectPitchHz(samples, sampleRate);

        Assert.NotNull(detected);
        Assert.InRange(detected!.Value, frequencyHz * 0.98, frequencyHz * 1.02);
    }

    [Fact]
    public void DetectPitchHz_Silence_ReturnsNull()
    {
        var samples = new float[4096];
        double? detected = PitchDetector.DetectPitchHz(samples, 44100);
        Assert.Null(detected);
    }
}
