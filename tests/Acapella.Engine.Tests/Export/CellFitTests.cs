using System.Diagnostics;
using Acapella.Engine.Composite;
using Acapella.Engine.Export;
using SkiaSharp;

namespace Acapella.Engine.Tests.Export;

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
