using System.Diagnostics;
using Acapella.Engine.Mix;

namespace Acapella.Engine.Tests.Mix;

public class AudioDecoderTests
{
    /// <summary>
    /// Regression test for a real hang: AudioDecoder redirected ffmpeg's stderr but never read
    /// it. A realistic-sized video (unlike a tiny synthetic fixture) produces enough libx264/aac
    /// log output on stderr to fill the OS pipe buffer, at which point ffmpeg blocks writing to
    /// stderr while we're blocked reading stdout -- a deadlock, reported as the app freezing on
    /// "Preview Mix" with two real video layers. Fixed by draining stderr on a background thread
    /// (FfmpegProcessUtil.DrainStderrInBackground). This test must complete well within the
    /// timeout to prove the fix, not just "usually finish fast."
    /// </summary>
    [Fact]
    public void DecodeToMonoFloat_RealisticSizedVideo_DoesNotHang()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-audiodecoder-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string videoPath = Path.Combine(tempDir, "realistic.mp4");

        try
        {
            RunFfmpeg("-y", "-f", "lavfi", "-i", "testsrc=size=640x480:rate=30:duration=8",
                      "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=8",
                      "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", videoPath);

            var task = Task.Run(() => AudioDecoder.DecodeToMonoFloat(videoPath, 44100));
            bool completed = task.Wait(TimeSpan.FromSeconds(20));

            Assert.True(completed, "AudioDecoder.DecodeToMonoFloat hung past the timeout -- stderr deadlock regression.");
            Assert.True(task.Result.Length > 0, "Decoded audio should not be empty.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

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
}
