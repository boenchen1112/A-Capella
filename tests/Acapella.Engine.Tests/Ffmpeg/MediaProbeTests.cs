using System.Diagnostics;
using Acapella.Engine.Ffmpeg;

namespace Acapella.Engine.Tests.Ffmpeg;

public class MediaProbeTests
{
    /// <summary>
    /// Regression test for M6: a failed recording (e.g. ffmpeg couldn't open the requested
    /// device) previously left a zombie layer in the app with a missing/empty file. MediaProbe is
    /// the check used to catch that before the layer is kept.
    /// </summary>
    [Fact]
    public void HasNonzeroDuration_MissingFile_ReturnsFalse()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"acapella-missing-{Guid.NewGuid()}.mkv");
        Assert.False(MediaProbe.HasNonzeroDuration(missingPath));
    }

    [Fact]
    public void HasNonzeroDuration_EmptyFile_ReturnsFalse()
    {
        string emptyPath = Path.Combine(Path.GetTempPath(), $"acapella-empty-{Guid.NewGuid()}.mkv");
        File.WriteAllBytes(emptyPath, Array.Empty<byte>());
        try
        {
            Assert.False(MediaProbe.HasNonzeroDuration(emptyPath));
        }
        finally
        {
            File.Delete(emptyPath);
        }
    }

    [Fact]
    public void HasNonzeroDuration_ValidClip_ReturnsTrue()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-mediaprobe-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string video = Path.Combine(tempDir, "clip.mp4");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            string[] args = { "-y", "-f", "lavfi", "-i", "color=c=red:s=32x32:r=10:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using (var process = Process.Start(psi)!)
            {
                process.StandardError.ReadToEnd();
                process.WaitForExit();
            }

            Assert.True(MediaProbe.HasNonzeroDuration(video));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
