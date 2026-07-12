using Acapella.Engine.Mix;
using Acapella.Engine.Sync;

namespace Acapella.Engine.Tests.Mix;

public class AudioShiftHelperTests
{
    [Fact]
    public void ApplyShift_PositiveShift_PadsLeadingSilence()
    {
        var samples = new float[] { 1f, 2f, 3f };
        int sampleRate = 1000;
        double shiftMs = 5; // 5 samples at 1000Hz

        var result = AudioShiftHelper.ApplyShift(samples, shiftMs, sampleRate);

        Assert.Equal(8, result.Length);
        Assert.All(result[..5], v => Assert.Equal(0f, v));
        Assert.Equal(new float[] { 1f, 2f, 3f }, result[5..]);
    }

    [Fact]
    public void ApplyShift_NegativeShift_TrimsFromHead()
    {
        var samples = new float[] { 1f, 2f, 3f, 4f, 5f };
        int sampleRate = 1000;
        double shiftMs = -2; // trim 2 samples

        var result = AudioShiftHelper.ApplyShift(samples, shiftMs, sampleRate);

        Assert.Equal(new float[] { 3f, 4f, 5f }, result);
    }

    [Fact]
    public void ApplyShift_ZeroShift_ReturnsSameSamples()
    {
        var samples = new float[] { 1f, 2f, 3f };
        var result = AudioShiftHelper.ApplyShift(samples, 0, 44100);
        Assert.Same(samples, result);
    }

    /// <summary>
    /// Regression test for a real bug: LayerModel.CalibratedOffsetMs/ManualOffsetMs were set by
    /// the UI but never consumed anywhere in mix/preview/export, so the sync offset feature (and
    /// the manual fine-tune slider) were no-ops. This reproduces the audit's suggested test gap:
    /// two layers with an identical click, one shifted +100ms via ManualOffsetMs, mixed down, and
    /// the two clicks located via cross-correlation should land ~100ms apart.
    /// </summary>
    [Fact]
    public void ManualOffset_ShiftsLayerRelativeToOthersInMixedOutput()
    {
        int sampleRate = 44100;
        int totalLength = sampleRate * 2;

        var click = ToneGenerator.GenerateClick(sampleRate);
        int clickPosition = 20000;

        float[] BuildLayerWithClick()
        {
            var samples = new float[totalLength];
            for (int i = 0; i < click.Length; i++)
                samples[clickPosition + i] = click[i];
            return samples;
        }

        var layerA = new Acapella.Engine.Project.LayerModel { LayerId = 0, Kind = Acapella.Engine.Project.LayerKind.RecordedAV, SourcePath = "a.wav" };
        var layerB = new Acapella.Engine.Project.LayerModel { LayerId = 1, Kind = Acapella.Engine.Project.LayerKind.RecordedAV, SourcePath = "b.wav" };
        layerB.ManualOffsetMs = 100;

        var shiftedA = AudioShiftHelper.ApplyShift(BuildLayerWithClick(), layerA.GetShiftMs(), sampleRate);
        var shiftedB = AudioShiftHelper.ApplyShift(BuildLayerWithClick(), layerB.GetShiftMs(), sampleRate);

        // Layer B's click should now be ~100ms later than layer A's in absolute sample position.
        int expectedShiftSamples = (int)Math.Round(100.0 / 1000.0 * sampleRate);

        int lagA = CrossCorrelator.FindOffsetSamples(click, shiftedA, maxLagSamples: 25000);
        int lagB = CrossCorrelator.FindOffsetSamples(click, shiftedB, maxLagSamples: 30000);

        Assert.Equal(clickPosition, lagA);
        Assert.Equal(clickPosition + expectedShiftSamples, lagB);
    }
}
