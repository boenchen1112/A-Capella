using A_Capella.Engine.Project;

namespace A_Capella.Engine.Tests.Project;

public class ManualOffsetTests
{
    private static LayerModel NewLayer() => new LayerCollection().Add(LayerKind.UploadedVideo, "a.mp4");

    [Fact]
    public void Nudge_AddsToTheOffset_InEitherDirection()
    {
        var layer = NewLayer();
        layer.NudgeManualOffset(10);
        layer.NudgeManualOffset(10);
        layer.NudgeManualOffset(-5);
        Assert.Equal(15, layer.ManualOffsetMs);
    }

    [Fact]
    public void Nudge_IsClampedToTheMaximum()
    {
        var layer = NewLayer();
        layer.NudgeManualOffset(1e9);
        Assert.Equal(LayerModel.MaxManualOffsetMs, layer.ManualOffsetMs);
        layer.NudgeManualOffset(-1e9);
        Assert.Equal(-LayerModel.MaxManualOffsetMs, layer.ManualOffsetMs);
    }

    [Fact]
    public void ManyFineSteps_DoNotAccumulateFloatingPointDust()
    {
        var layer = NewLayer();
        for (int i = 0; i < 10; i++) layer.NudgeManualOffset(0.1);
        Assert.Equal(1.0, layer.ManualOffsetMs);
    }

    [Fact]
    public void Nudge_ChangesTheShift_ThatPreviewAndExportApply()
    {
        var layer = NewLayer();
        layer.CalibratedOffsetMs = 100;
        layer.NudgeManualOffset(30);
        Assert.Equal(-70, layer.GetShiftMs());
    }

    [Fact]
    public void Nudge_ChangesTheAudioCacheKey_SoTheMixIsRebuilt()
    {
        var layer = NewLayer();
        string before = layer.SourceCacheKey();
        layer.NudgeManualOffset(10);
        Assert.NotEqual(before, layer.SourceCacheKey());
    }

    [Fact]
    public void Retake_ResetsTheNudge()
    {
        var layer = NewLayer();
        layer.NudgeManualOffset(40);
        layer.ReplaceSource(LayerKind.UploadedVideo, "b.mp4", 0);
        Assert.Equal(0, layer.ManualOffsetMs);
    }
}
