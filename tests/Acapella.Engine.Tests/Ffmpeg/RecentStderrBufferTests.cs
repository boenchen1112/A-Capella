using Acapella.Engine.Ffmpeg;

namespace Acapella.Engine.Tests.Ffmpeg;

public class RecentStderrBufferTests
{
    /// <summary>Regression test for audit B4: CaptureStarted must signal once ffmpeg's stderr
    /// shows a per-frame progress line ("frame=..."), not just any output line -- this is what
    /// lets a caller distinguish "process launched" from "capture is actually producing frames".</summary>
    [Fact]
    public void CaptureStarted_SignalsOnlyAfterFrameProgressLine()
    {
        var buffer = new RecentStderrBuffer(maxLines: 10);

        Assert.False(buffer.CaptureStarted.IsSet);

        buffer.Add("ffmpeg version 6.0 Copyright (c) 2000-2023 the FFmpeg developers");
        buffer.Add("Input #0, dshow, from 'video=Fake Camera:audio=Fake Mic':");
        Assert.False(buffer.CaptureStarted.IsSet, "Should not signal before a frame progress line.");

        buffer.Add("frame=    1 fps=0.0 q=28.0 size=       0kB time=00:00:00.03 bitrate=   9.8kbits/s speed=0.06x");
        Assert.True(buffer.CaptureStarted.IsSet, "Should signal once a frame progress line appears.");
    }

    [Fact]
    public void GetLines_ReturnsMostRecentLinesUpToMaxLines()
    {
        var buffer = new RecentStderrBuffer(maxLines: 2);
        buffer.Add("line1");
        buffer.Add("line2");
        buffer.Add("line3");

        var lines = buffer.GetLines();

        Assert.Equal(new[] { "line2", "line3" }, lines);
    }
}
