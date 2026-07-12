using System.Diagnostics;
using Acapella.Engine.Export;

namespace Acapella.Engine.Tests.Export;

public class LayerFrameSourceTests
{
    // VideoFrameStreamSource.Dispose() kills the ffmpeg process, but the OS can take a moment to
    // release the file handle on the source clip after Kill() returns; retry cleanup briefly
    // rather than flake on an unrelated IOException.
    private static void DeleteWithRetry(string tempDir)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string CreateRedClipFixture(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"acapella-framesource-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string video = Path.Combine(tempDir, "red.mp4");

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] args = { "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:r=10:d=1" , "-c:v", "libx264", "-pix_fmt", "yuv420p", video };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg fixture generation failed");

        return video;
    }

    /// <summary>
    /// Regression test for C2 (video half): a positive shiftMs must hold the placeholder frame
    /// before decoding starts, so the video stays aligned with its own layer's audio (which gets
    /// leading silence padding via AudioShiftHelper for the same shiftMs).
    /// </summary>
    [Fact]
    public void PositiveShift_HoldsPlaceholderFrameBeforeDecoding()
    {
        string video = CreateRedClipFixture(out string tempDir);
        try
        {
            int fps = 10;
            double shiftMs = 500; // 5 frames at 10fps
            using var source = new VideoFrameStreamSource(video, 32, 32, fps, shiftMs);

            for (int i = 0; i < 5; i++)
            {
                var frame = source.GetNextFrame();
                var pixel = frame.GetPixel(16, 16);
                Assert.True(pixel.Red < 50, $"Expected held placeholder (black) frame at index {i}, got {pixel}.");
            }

            var realFrame = source.GetNextFrame();
            var realPixel = realFrame.GetPixel(16, 16);
            Assert.True(realPixel.Red > 150, $"Expected red decoded frame after hold, got {realPixel}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>
    /// Regression test for M1: a plain scale=w:h stretched non-matching-aspect-ratio source
    /// video to fill the cell, distorting it (e.g. a 16:9 webcam feed squashed into a square
    /// cell). Decoding a 16:9 source into a square cell should letterbox (black bars top/bottom)
    /// rather than stretch -- verified by checking the padded strip is black while the center
    /// still shows the source content.
    /// </summary>
    [Fact]
    public void WideAspectRatioSource_LettersboxesRatherThanStretching()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-aspect-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string video = Path.Combine(tempDir, "wide.mp4");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // 16:9 source (160x90) decoded into a 64x64 (1:1) cell.
            string[] args = { "-y", "-f", "lavfi", "-i", "color=c=red:s=160x90:r=10:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var process = Process.Start(psi)!)
            {
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg fixture generation failed");
            }

            using var source = new VideoFrameStreamSource(video, 64, 64, fps: 10);
            var frame = source.GetNextFrame();

            // Letterboxed: top/bottom strips should be black padding, center should be red content.
            var topPixel = frame.GetPixel(32, 4);
            var centerPixel = frame.GetPixel(32, 32);

            Assert.True(topPixel.Red < 50 && topPixel.Green < 50 && topPixel.Blue < 50,
                $"Expected black letterbox padding at top, got {topPixel}.");
            Assert.True(centerPixel.Red > 150, $"Expected red source content at center, got {centerPixel}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>
    /// Regression test for C2 (video half): a negative shiftMs must skip ahead into the layer's
    /// own footage via -ss, mirroring AudioShiftHelper trimming the same layer's audio head.
    /// </summary>
    [Fact]
    public void NegativeShift_DoesNotThrowAndProducesFrames()
    {
        string video = CreateRedClipFixture(out string tempDir);
        try
        {
            int fps = 10;
            double shiftMs = -300;
            using var source = new VideoFrameStreamSource(video, 32, 32, fps, shiftMs);

            var frame = source.GetNextFrame();
            var pixel = frame.GetPixel(16, 16);
            Assert.True(pixel.Red > 150, $"Expected red decoded frame from skipped-ahead footage, got {pixel}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }
}
