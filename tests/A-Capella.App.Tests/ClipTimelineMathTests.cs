using A_Capella.App.Layout;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Pure math, no WPF objects involved -- an ordinary [Fact], not [StaFact].</summary>
public class ClipTimelineMathTests
{
    [Fact]
    public void ToPixelRange_MapsSpanProportionallyAcrossTheTrack()
    {
        var (x, width) = ClipTimelineMath.ToPixelRange(1000, 3000, timelineDurationMs: 10000, trackWidthPx: 1000);
        Assert.Equal(100, x, 3);
        Assert.Equal(200, width, 3);
    }

    [Fact]
    public void ToPixelRange_DegenerateScale_ReturnsZeroZero()
    {
        Assert.Equal((0, 0), ClipTimelineMath.ToPixelRange(0, 100, 0, 500));
        Assert.Equal((0, 0), ClipTimelineMath.ToPixelRange(0, 100, 1000, 0));
    }

    [Fact]
    public void PixelDeltaToMs_ConvertsAtTrackScale()
    {
        double ms = ClipTimelineMath.PixelDeltaToMs(deltaPx: 50, timelineDurationMs: 10000, trackWidthPx: 1000);
        Assert.Equal(500, ms, 3);
    }

    [Fact]
    public void PixelDeltaToMs_NotYetLaidOut_ReturnsZero()
    {
        Assert.Equal(0, ClipTimelineMath.PixelDeltaToMs(50, 10000, 0));
    }

    [Fact]
    public void ClampTrimStart_NeverGoesNegative()
    {
        Assert.Equal(0, ClipTimelineMath.ClampTrimStart(-500, trimEndMs: 5000));
    }

    [Fact]
    public void ClampTrimStart_RespectsMinGapFromTrimEnd()
    {
        double clamped = ClipTimelineMath.ClampTrimStart(4990, trimEndMs: 5000);
        Assert.Equal(5000 - ClipTimelineMath.MinTrimGapMs, clamped);
    }

    [Fact]
    public void ClampTrimEnd_NeverExceedsSourceDuration()
    {
        double clamped = ClipTimelineMath.ClampTrimEnd(999999, trimStartMs: 0, sourceDurationMs: 10000);
        Assert.Equal(10000, clamped);
    }

    [Fact]
    public void ClampTrimEnd_RespectsMinGapFromTrimStart()
    {
        double clamped = ClipTimelineMath.ClampTrimEnd(10, trimStartMs: 0, sourceDurationMs: 10000);
        Assert.Equal(ClipTimelineMath.MinTrimGapMs, clamped);
    }

    [Fact]
    public void ClampTrimEnd_UnknownDuration_DoesNotCollapseTheClipToTheMinimumGap()
    {
        // sourceDurationMs <= 0 means "not known yet" (e.g. MediaProbe hasn't returned) -- a naive
        // clamp against 0 would snap every out-handle drag down to trimStartMs + MinTrimGapMs.
        // Callers should disable the handle until a real duration is known, but the math itself must
        // not be destructive if it's ever invoked first.
        double clamped = ClipTimelineMath.ClampTrimEnd(8000, trimStartMs: 1000, sourceDurationMs: 0);
        Assert.Equal(8000, clamped);
    }

    // ----- Ruler ticks -----

    [Fact]
    public void ComputeTickTimesMs_DegenerateScale_ReturnsEmpty()
    {
        Assert.Empty(ClipTimelineMath.ComputeTickTimesMs(0, 1000));
        Assert.Empty(ClipTimelineMath.ComputeTickTimesMs(1000, 0));
    }

    [Fact]
    public void ComputeTickTimesMs_PicksThirtySecondInterval_WhenNarrowerIntervalsWouldCrowd()
    {
        // 2 minutes across 360px: 10s/15s intervals land under the 60px minimum spacing (30px/45px),
        // so 30s (90px apart) is the first "nice" interval that clears it.
        var ticks = ClipTimelineMath.ComputeTickTimesMs(durationMs: 120000, trackWidthPx: 360);
        Assert.Equal(new double[] { 0, 30000, 60000, 90000, 120000 }, ticks);
    }

    [Fact]
    public void ComputeTickTimesMs_NeverPacksTicksCloserThanTheMinimumSpacing()
    {
        var ticks = ClipTimelineMath.ComputeTickTimesMs(durationMs: 600000, trackWidthPx: 500, minPxBetweenTicks: 60);
        double pxPerMs = 500.0 / 600000;
        for (int i = 1; i < ticks.Count; i++)
            Assert.True((ticks[i] - ticks[i - 1]) * pxPerMs >= 60 - 0.001);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(30000, "0:30")]
    [InlineData(65000, "1:05")]
    public void FormatTickLabel_FormatsAsMinutesSeconds(double ms, string expected)
    {
        Assert.Equal(expected, ClipTimelineMath.FormatTickLabel(ms));
    }
}

/// <summary>ClipTrimDrag: pure math, no WPF objects involved.</summary>
public class ClipTrimDragTests
{
    [Fact]
    public void ValueAt_NoMovement_ReturnsTheStartingValue()
    {
        var drag = new ClipTrimDrag(ClipTrimDrag.Handle.In, startValueMs: 1000, otherBoundMs: 9000,
            sourceDurationMs: 10000, timelineDurationMs: 10000, trackWidthPx: 1000);

        double value = drag.ValueAt(0);

        Assert.Equal(1000, value);
        Assert.False(drag.HasChangedFrom(value));
    }

    [Fact]
    public void ValueAt_In_MovesWithThePointer_AndClampsToZero()
    {
        var drag = new ClipTrimDrag(ClipTrimDrag.Handle.In, startValueMs: 1000, otherBoundMs: 9000,
            sourceDurationMs: 10000, timelineDurationMs: 10000, trackWidthPx: 1000);

        Assert.Equal(1500, drag.ValueAt(50));     // +50px * (10000ms/1000px) = +500ms
        Assert.Equal(0, drag.ValueAt(-5000));     // dragged far left: clamped to 0, never negative
    }

    [Fact]
    public void ValueAt_In_NeverCrossesTheMinimumGapFromOut()
    {
        var drag = new ClipTrimDrag(ClipTrimDrag.Handle.In, startValueMs: 1000, otherBoundMs: 1020,
            sourceDurationMs: 10000, timelineDurationMs: 10000, trackWidthPx: 1000);

        double value = drag.ValueAt(5000); // a huge rightward drag

        Assert.Equal(1020 - ClipTimelineMath.MinTrimGapMs, value);
    }

    [Fact]
    public void ValueAt_Out_MovesWithThePointer_AndClampsToSourceDuration()
    {
        var drag = new ClipTrimDrag(ClipTrimDrag.Handle.Out, startValueMs: 9000, otherBoundMs: 1000,
            sourceDurationMs: 10000, timelineDurationMs: 10000, trackWidthPx: 1000);

        Assert.Equal(8500, drag.ValueAt(-50));
        Assert.Equal(10000, drag.ValueAt(5000)); // dragged far right: clamped to the source's own duration
    }

    [Fact]
    public void HasChangedFrom_DetectsARealChangeAfterTheDragReturnsNearItsStart()
    {
        var drag = new ClipTrimDrag(ClipTrimDrag.Handle.In, startValueMs: 1000, otherBoundMs: 9000,
            sourceDurationMs: 10000, timelineDurationMs: 10000, trackWidthPx: 1000);

        Assert.True(drag.HasChangedFrom(1500));
        Assert.False(drag.HasChangedFrom(1000));
    }
}
