using Acapella.Engine.Pitch;

namespace Acapella.Engine.Tests.Pitch;

public class AutoPitchCorrectorTests
{
    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, int length)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
            samples[i] = (float)(0.8 * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        return samples;
    }

    [Fact]
    public void Correct_FlattenedPitch_EndsUpCloserToTargetNote()
    {
        int sampleRate = 44100;
        double flattenedHz = 430.0; // deliberately flat vs A4 (440Hz), still within A4's snap range
        var input = GenerateSineWave(flattenedHz, sampleRate, sampleRate * 2);

        var corrector = new AutoPitchCorrector();
        var corrected = corrector.Correct(input, sampleRate);

        double? inputDetected = AutoPitchCorrector.DetectOverallPitch(input, sampleRate);
        double? correctedDetected = AutoPitchCorrector.DetectOverallPitch(corrected, sampleRate);

        Assert.NotNull(inputDetected);
        Assert.NotNull(correctedDetected);

        double targetHz = 440.0;
        double inputError = Math.Abs(inputDetected!.Value - targetHz);
        double correctedError = Math.Abs(correctedDetected!.Value - targetHz);

        Assert.True(correctedError < inputError,
            $"Expected corrected pitch ({correctedDetected}Hz) to be closer to {targetHz}Hz than input ({inputDetected}Hz).");
    }
}
