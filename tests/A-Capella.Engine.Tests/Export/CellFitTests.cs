using System.Diagnostics;
using A_Capella.Engine.Composite;
using A_Capella.Engine.Export;
using SkiaSharp;

namespace A_Capella.Engine.Tests.Export;

public class CellFitTests
{
    [Fact]
    public void Fit_BuildsAPadChain_AndFillBuildsACropChain()
    {
        string fit = CellVideoFilter.Build(100, 50, 30, CellFit.Fit);
        string fill = CellVideoFilter.Build(100, 50, 30, CellFit.Fill);

        Assert.Equal("scale=100:50:force_original_aspect_ratio=decrease,pad=100:50:(ow-iw)/2:(oh-ih)/2:black,fps=30", fit);
        Assert.Equal("scale=100:50:force_original_aspect_ratio=increase,crop=100:50,fps=30", fill);
    }

    [Fact]
    public void Mirror_PrependsAHorizontalFlip_InBothModes()
    {
        Assert.StartsWith("hflip,scale=", CellVideoFilter.Build(100, 50, 30, CellFit.Fill, mirror: true));
        Assert.StartsWith("hflip,scale=", CellVideoFilter.Build(100, 50, 30, CellFit.Fit, mirror: true));
        Assert.DoesNotContain("hflip", CellVideoFilter.Build(100, 50, 30, CellFit.Fit));
    }

    /// <summary>Left half red, right half blue: mirrored, the colors swap sides.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void VideoFrameStreamSource_MirrorSwapsLeftAndRight(bool mirror, bool redOnLeft)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"acapella-mirror-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "split.mp4");
            var psi = new ProcessStartInfo { FileName = "ffmpeg", UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:r=10:d=1[l];color=c=blue:s=32x32:r=10:d=1[r];[l][r]hstack", "-c:v", "libx264", "-pix_fmt", "yuv420p", video })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

            using var source = new VideoFrameStreamSource(video, 64, 32, 10, mirror: mirror);
            var frame = source.GetNextFrame();
            var left = frame.GetPixel(8, 16);
            var right = frame.GetPixel(56, 16);

            if (redOnLeft) { Assert.True(left.Red > 200 && right.Blue > 200, $"left {left}, right {right}"); }
            else { Assert.True(left.Blue > 200 && right.Red > 200, $"left {left}, right {right}"); }
        }
        finally
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                catch (IOException) { Thread.Sleep(100); }
            }
        }
    }

    /// <summary>A portrait clip with a red square in the middle, shown in a landscape cell: in both
    /// modes the square must stay square, never stretched to the cell's shape.</summary>
    [Theory]
    [InlineData(CellFit.Fit)]
    [InlineData(CellFit.Fill)]
    public void VideoFrameStreamSource_PortraitClipInLandscapeCell_IsNotDistorted(CellFit fit)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"acapella-portrait-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "portrait.mp4");
            var psi = new ProcessStartInfo { FileName = "ffmpeg", UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-f", "lavfi", "-i", "color=c=gray:s=72x128:r=10:d=1,drawbox=x=26:y=54:w=20:h=20:color=red:t=fill", "-c:v", "libx264", "-pix_fmt", "yuv420p", video })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

            using var source = new VideoFrameStreamSource(video, 128, 72, 10, fit: fit);
            var frame = source.GetNextFrame();

            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    var c = frame.GetPixel(x, y);
                    if (c.Red > 180 && c.Green < 90 && c.Blue < 90)
                    {
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                }
            Assert.True(maxX >= 0, "the red square should be visible");
            double ratio = (maxX - minX + 1) / (double)(maxY - minY + 1);
            Assert.InRange(ratio, 0.85, 1.15);
        }
        finally
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                catch (IOException) { Thread.Sleep(100); }
            }
        }
    }

    /// <summary>A solid-red 64x32 clip shown in a 32x32 cell: Fit leaves black bars above and below,
    /// Fill covers every pixel with red.</summary>
    [Theory]
    [InlineData(CellFit.Fit, true)]
    [InlineData(CellFit.Fill, false)]
    public void VideoFrameStreamSource_AppliesTheFitMode(CellFit fit, bool expectBlackBars)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"acapella-fit-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "wide.mp4");
            var psi = new ProcessStartInfo { FileName = "ffmpeg", UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in new[] { "-y", "-f", "lavfi", "-i", "color=c=red:s=64x32:r=10:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video })
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi)!) { p.StandardError.ReadToEnd(); p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

            using var source = new VideoFrameStreamSource(video, 32, 32, 10, fit: fit);
            var frame = source.GetNextFrame();

            var top = frame.GetPixel(16, 1);
            var middle = frame.GetPixel(16, 16);
            Assert.True(middle.Red > 200 && middle.Green < 60, "center should be red");
            if (expectBlackBars)
                Assert.True(top.Red < 40, $"expected a black bar at the top, got {top}");
            else
                Assert.True(top.Red > 200, $"expected red all the way to the edge, got {top}");
        }
        finally
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                catch (IOException) { Thread.Sleep(100); }
            }
        }
    }
}
