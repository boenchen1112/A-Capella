using System.Diagnostics;
using Acapella.Engine.Sync;

namespace Acapella.Engine.Tests.Sync;

public class AvSyncAnalyzerTests
{
    private static void RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg fixture generation failed (args: {string.Join(' ', args)})");
    }

    /// <summary>
    /// Regression test for a real bug: GetVideoFlashSeconds divided the decoded frame index by a
    /// caller-supplied frameRate without ever forcing ffmpeg to actually decode at that rate,
    /// so the computed time was silently wrong for any real capture whose native rate isn't
    /// exactly 30fps. This fixture is deliberately 24fps (not the 30fps default) with a white
    /// flash starting at frame 12 (0.5s); if the fps weren't forced to match, the same frame
    /// index divided by the wrong assumed rate would report the wrong time.
    /// </summary>
    [Fact]
    public void GetVideoFlashSeconds_NonDefaultFrameRate_ReportsCorrectTime()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-avsync-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string videoPath = Path.Combine(tempDir, "flash.mp4");

        try
        {
            double fps = 24.0;
            // Black for 0.5s, then white for the remainder -- flash starts at frame 12 (0.5s @ 24fps).
            string filter = $"color=c=black:s=64x64:r={fps}:d=0.5[a];color=c=white:s=64x64:r={fps}:d=0.5[b];[a][b]concat=n=2:v=1:a=0";
            RunFfmpeg("-y", "-f", "lavfi", "-i", filter, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-r", fps.ToString(System.Globalization.CultureInfo.InvariantCulture), videoPath);

            double flashSeconds = AvSyncAnalyzer.GetVideoFlashSeconds(videoPath, frameRate: fps);

            Assert.InRange(flashSeconds, 0.4, 0.6);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
