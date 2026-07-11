using System.Diagnostics;
using Acapella.Engine.Export;
using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Export;

public class ExportEngineTests
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

    private static (string video1, string video2, string tempDir) CreateFixtureClips()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-export-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        string video1 = Path.Combine(tempDir, "layer0.mp4");
        string video2 = Path.Combine(tempDir, "layer1.mp4");

        RunFfmpeg("-y", "-f", "lavfi", "-i", "color=c=red:s=64x64:r=10:d=2",
                  "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2",
                  "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video1);

        RunFfmpeg("-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=10:d=1",
                  "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=44100:duration=1",
                  "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video2);

        return (video1, video2, tempDir);
    }

    private static string ProbeStreamInfo(string mediaPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration:stream=codec_type,codec_name");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1");
        psi.ArgumentList.Add(mediaPath);

        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void Export_ProducesValidPlayableMp4WithAudioAndVideoStreams()
    {
        var (video1, video2, tempDir) = CreateFixtureClips();
        string outputPath = Path.Combine(tempDir, "export.mp4");

        try
        {
            var layers = new LayerCollection();
            layers.Add(LayerKind.RecordedAV, video1);
            layers.Add(LayerKind.RecordedAV, video2);

            var exportEngine = new ExportEngine();
            exportEngine.Export(layers, outputPath, width: 128, height: 128, fps: 10, sampleRate: 44100);

            Assert.True(File.Exists(outputPath), "Export did not produce an output file.");
            Assert.True(new FileInfo(outputPath).Length > 0, "Exported file is empty.");

            string raw = ProbeStreamInfo(outputPath);

            Assert.Contains("codec_type=video", raw);
            Assert.Contains("codec_type=audio", raw);
            Assert.Contains("codec_name=h264", raw);
            Assert.Contains("codec_name=aac", raw);

            // Duration should match the longer of the two fixture clips (2s), within tolerance
            // for encoder rounding.
            var durationLine = raw.Split('\n').FirstOrDefault(l => l.StartsWith("duration="));
            Assert.NotNull(durationLine);
            double duration = double.Parse(durationLine!.Split('=')[1]);
            Assert.InRange(duration, 1.5, 2.5);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
