using Acapella.App.Layout;
using Xunit;

namespace Acapella.App.Tests;

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
}
