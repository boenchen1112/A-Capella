using System.Diagnostics;
using Acapella.Engine.Export;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
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

            var exportEngine = new ExportEngine(hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
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

    /// <summary>
    /// Regression test for a real color-channel-swap bug: Compositor created its output bitmap
    /// with the platform default color type (Bgra8888 on Windows), then ExportEngine piped those
    /// raw bytes into ffmpeg declared as rgba -- every exported frame had red and blue swapped.
    /// GetPixel-based assertions are color-type-aware and cannot catch this; this test decodes the
    /// actual exported raw bytes to prove the byte order on disk is correct.
    /// </summary>
    [Fact]
    public void Export_SolidRedFixture_OutputFrameIsRedDominant()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-export-color-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string video = Path.Combine(tempDir, "red.mp4");
        string outputPath = Path.Combine(tempDir, "export.mp4");

        try
        {
            RunFfmpeg("-y", "-f", "lavfi", "-i", "color=c=red:s=64x64:r=10:d=1",
                      "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1",
                      "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video);

            var layers = new LayerCollection();
            layers.Add(LayerKind.RecordedAV, video);

            var exportEngine = new ExportEngine(hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            exportEngine.Export(layers, outputPath, width: 64, height: 64, fps: 10, sampleRate: 44100);

            byte[] frameBytes = DecodeFirstRawFrame(outputPath, 64, 64);

            // The single layer occupies only the top-left quadrant (2x2 grid layout with 1
            // layer); sample a pixel inside that cell rather than averaging the whole frame,
            // which is otherwise mostly black background.
            int width = 64;
            int x = 16, y = 16;
            int pixelIndex = (y * width + x) * 4;
            byte r = frameBytes[pixelIndex];
            byte g = frameBytes[pixelIndex + 1];
            byte b = frameBytes[pixelIndex + 2];

            Assert.True(r > 150, $"Expected red-dominant pixel, got r={r}, g={g}, b={b}");
            Assert.True(r > b + 50, $"Red should dominate blue, got r={r}, b={b}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>Regression test for audit B8: grid placement must follow LayerModel.CellIndex, not
    /// LayerCollection's internal insertion order -- simulates a layer being attached to sidebar
    /// row 2 (CellIndex 1) before row 1 (CellIndex 0) gets its source, which used to render in the
    /// wrong cell because export/preview both zipped frames to cellRects by list order.</summary>
    [Fact]
    public void Export_LayersAddedOutOfCellOrder_PlacesEachInItsCellIndexNotInsertionOrder()
    {
        var (video1, video2, tempDir) = CreateFixtureClips(); // video1=red (2s), video2=blue (1s)
        string outputPath = Path.Combine(tempDir, "export.mp4");

        try
        {
            var layers = new LayerCollection();
            // Inserted red-then-blue, but assigned to the grid as if row 2 (blue, CellIndex 0,
            // top-left) was filled before row 1 (red, CellIndex 1, top-right).
            var redLayer = layers.Add(LayerKind.RecordedAV, video1);
            var blueLayer = layers.Add(LayerKind.RecordedAV, video2);
            redLayer.CellIndex = 1;
            blueLayer.CellIndex = 0;

            var exportEngine = new ExportEngine(hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            exportEngine.Export(layers, outputPath, width: 128, height: 128, fps: 10, sampleRate: 44100);

            byte[] frameBytes = DecodeFirstRawFrame(outputPath, 128, 128);
            AssertPixelColorDominant(frameBytes, 128, x: 32, y: 32, redDominant: false);   // top-left: blue
            AssertPixelColorDominant(frameBytes, 128, x: 96, y: 32, redDominant: true);    // top-right: red
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void AssertPixelColorDominant(byte[] frameBytes, int width, int x, int y, bool redDominant)
    {
        int pixelIndex = (y * width + x) * 4;
        byte r = frameBytes[pixelIndex];
        byte b = frameBytes[pixelIndex + 2];
        if (redDominant)
            Assert.True(r > b + 50, $"Expected red-dominant pixel at ({x},{y}), got r={r}, b={b}");
        else
            Assert.True(b > r + 50, $"Expected blue-dominant pixel at ({x},{y}), got r={r}, b={b}");
    }

    /// <summary>Regression test for audit A5: a video-only layer (no audio track) decodes to
    /// zero audio samples, but its duration must come from the video stream, not audio -- export
    /// used to throw "No decodable audio found" for this case.</summary>
    [Fact]
    public void Export_VideoOnlyLayer_HonorsVideoDurationAndSucceeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-export-videoonly-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string video = Path.Combine(tempDir, "silent.mp4");
        string outputPath = Path.Combine(tempDir, "export.mp4");

        try
        {
            RunFfmpeg("-y", "-f", "lavfi", "-i", "color=c=green:s=64x64:r=10:d=2",
                      "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", video);

            var layers = new LayerCollection();
            layers.Add(LayerKind.UploadedVideo, video);

            var exportEngine = new ExportEngine(hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            exportEngine.Export(layers, outputPath, width: 64, height: 64, fps: 10, sampleRate: 44100);

            Assert.True(File.Exists(outputPath), "Export did not produce an output file.");
            Assert.True(new FileInfo(outputPath).Length > 0, "Exported file is empty.");

            string raw = ProbeStreamInfo(outputPath);
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

    /// <summary>Regression test for P1 task 4: preview and export must sound the same at a given
    /// master volume, since both route the final mix through MixEngine.BuildMix's bus chain
    /// (master gain + brick-wall limiter) instead of export applying its own separate
    /// content-dependent PeakNormalizer pass. Compares export's actual mixdown RMS (decoded back
    /// from the lossy AAC output) against directly building the same mix in-process via
    /// MixEngine -- the "preview" path -- allowing tolerance for AAC lossy round-trip.</summary>
    [Fact]
    public void Export_AudioRms_MatchesDirectMixEngineBuild_ForSameMasterVolume()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-export-rms-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string video = Path.Combine(tempDir, "tone.mp4");
        string outputPath = Path.Combine(tempDir, "export.mp4");
        const int sampleRate = 44100;

        try
        {
            RunFfmpeg("-y", "-f", "lavfi", "-i", "color=c=red:s=64x64:r=10:d=1",
                      "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={sampleRate}:duration=1:beep_factor=0",
                      "-af", "volume=0.2",
                      "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video);

            var layers = new LayerCollection();
            var layer = layers.Add(LayerKind.RecordedAV, video);
            const float masterVolumeDb = 6f;

            var exportEngine = new ExportEngine(hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            exportEngine.Export(layers, outputPath, width: 64, height: 64, fps: 10, sampleRate: sampleRate, masterVolumeDb: masterVolumeDb);

            float[] exportedAudio = AudioDecoder.DecodeToMonoFloat(outputPath, sampleRate);
            float exportedRms = ComputeRms(exportedAudio);

            float[] sourceAudio = AudioDecoder.DecodeToMonoFloat(video, sampleRate);
            var mixEngine = new MixEngine(NoHostedPluginsAvailable.Instance);
            var mixInputs = new[] { new MixLayerInput(layer.LayerId, sourceAudio, sampleRate, layer.MixParameters, layer.SourceCacheKey()) };
            var directMix = mixEngine.BuildMix(mixInputs, sampleRate, masterVolumeDb);

            var directBuffer = new float[sourceAudio.Length * 2];
            int totalRead = 0;
            while (totalRead < directBuffer.Length)
            {
                int n = directMix.Read(directBuffer, totalRead, directBuffer.Length - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            // Interleaved stereo -> mono, using ffmpeg's own stereo-to-mono downmix normalization
            // ((L+R)/sqrt(2), not a plain average) so this matches how AudioDecoder.DecodeToMonoFloat
            // (used on the exported side, via "-ac 1") actually downmixes -- a plain average would
            // differ from ffmpeg's output by sqrt(2) even when both mixes are otherwise identical.
            var directMono = new float[directBuffer.Length / 2];
            for (int i = 0; i < directMono.Length; i++)
                directMono[i] = (float)((directBuffer[i * 2] + directBuffer[i * 2 + 1]) / Math.Sqrt(2));
            float directRms = ComputeRms(directMono);

            float ratio = exportedRms / directRms;
            Assert.InRange(ratio, 0.7f, 1.3f);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static float ComputeRms(float[] samples)
    {
        double sumSquares = samples.Sum(s => (double)s * s);
        return (float)Math.Sqrt(sumSquares / samples.Length);
    }

    private static byte[] DecodeFirstRawFrame(string mediaPath, int width, int height)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-vframes"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("rgba");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)!;
        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        var bytes = ms.ToArray();
        int expectedSize = width * height * 4;
        Assert.True(bytes.Length >= expectedSize, "Failed to decode a raw frame from exported output.");
        return bytes[..expectedSize];
    }
}
