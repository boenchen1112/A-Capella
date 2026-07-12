using Acapella.Engine.Pitch;
using Acapella.Engine.Sync;

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

    /// <summary>
    /// A bug audit assumed Rubber Band's realtime mode adds a fixed start delay (via a
    /// GetStartDelay() API) that would need trimming to keep corrected layers time-aligned. That
    /// method isn't exposed by the bundled RubberBandSharp 0.0.6 wrapper at all, and empirical
    /// probing (embedding a click at a known sample index, running it through
    /// AutoPitchCorrector.ApplyPitchShift, and cross-correlating to find where it landed) showed
    /// well under a millisecond of drift and no output-length mismatch -- the wrapper already
    /// handles this internally. This test locks that behavior in as a regression guard rather
    /// than applying an unnecessary/non-compiling "fix".
    /// </summary>
    [Fact]
    public void ApplyPitchShift_ClickAtKnownPosition_StaysAligned()
    {
        int sampleRate = 44100;
        int totalLength = sampleRate * 2;
        var samples = new float[totalLength];
        for (int i = 0; i < totalLength; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 430.0 * i / sampleRate));

        int clickStart = 20000;
        var click = ToneGenerator.GenerateClick(sampleRate);
        for (int i = 0; i < click.Length; i++)
            samples[clickStart + i] += click[i];

        double pitchScale = 440.0 / 430.0;
        var corrected = AutoPitchCorrector.ApplyPitchShift(samples, sampleRate, pitchScale);

        Assert.Equal(samples.Length, corrected.Length);

        int bestLag = CrossCorrelator.FindOffsetSamples(click, corrected, maxLagSamples: 25000);
        double errorMs = Math.Abs(bestLag - clickStart) * 1000.0 / sampleRate;

        Assert.True(errorMs < 15.0, $"Expected corrected click to stay within 15ms of its original position, drifted {errorMs:F2}ms.");
    }
}
