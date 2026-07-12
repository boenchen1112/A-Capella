using System.Diagnostics;
using Acapella.Engine.Export;
using Acapella.Engine.Preview;
using Acapella.Engine.Project;
using NAudio.Wave;
using SkiaSharp;

namespace Acapella.Engine.Tests.Preview;

/// <summary>No-op sink so tests exercise the frame-loop/lifecycle without a real WASAPI device.</summary>
public class FakeAudioSink : IPreviewAudioSink
{
    public void Play(ISampleProvider mix) { }
    public void Stop() { }
    public void Dispose() { }
}

public class PreviewPlaybackEngineTests
{
    // VideoFrameStreamSource.Dispose() kills the ffmpeg process, but the OS can take a moment to
    // release the file handle on the source clip after Kill() returns; retry cleanup briefly
    // rather than flake on an unrelated IOException (same pattern as LayerFrameSourceTests).
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

    private static (string path, string tempDir) CreateFixtureClip(string color, int durationSeconds)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-preview-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "layer.mp4");

        RunFfmpeg("-y", "-f", "lavfi", "-i", $"color=c={color}:s=64x64:r=10:d={durationSeconds}",
                  "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={durationSeconds}",
                  "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path);

        return (path, tempDir);
    }

    /// <summary>Fixture whose video content changes color at a known timestamp, so a test can
    /// tell whether a frame source is showing "early" (red) or "late" (blue) source content --
    /// distinguishing preview/export negative-shift math bugs that a solid-color clip can't
    /// reveal (audit A3).</summary>
    private static (string path, string tempDir) CreateColorChangeFixtureClip(double switchAtSeconds, int durationSeconds)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-preview-parity-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "layer.mp4");

        string geq = $"r='if(lt(T\\,{switchAtSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})\\,255\\,0)':g=0:b='if(gte(T\\,{switchAtSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})\\,255\\,0)'";
        RunFfmpeg("-y", "-f", "lavfi", "-i", $"color=c=black:s=64x64:r=10:d={durationSeconds}",
                  "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={durationSeconds}",
                  "-vf", $"geq={geq}",
                  "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path);

        return (path, tempDir);
    }

    /// <summary>Regression test for audit A3: a layer with a negative sync shift (the normal case
    /// for a recorded layer, GetShiftMs() = ManualOffsetMs - CalibratedOffsetMs) must show the
    /// same skipped-ahead video content in the preview's frame-0 render as export's frame-0
    /// render. Before the fix, preview's CreateFrameSource dropped the head-skip term, so preview
    /// showed the source's un-skipped ("early") content while export correctly skipped ahead.</summary>
    [Fact]
    public void PreviewFrameAtPositionZero_MatchesExportFrameZero_ForNegativeShiftLayer()
    {
        var (path, tempDir) = CreateColorChangeFixtureClip(switchAtSeconds: 0.2, durationSeconds: 2);
        try
        {
            var layer = new LayerModel
            {
                LayerId = 0,
                Kind = LayerKind.RecordedAV,
                SourcePath = path,
                CalibratedOffsetMs = 200, // GetShiftMs() = 0 - 200 = -200: skip ahead 200ms.
            };

            // Preview path: mirror PreviewPlaybackEngine.CreateFrameSource's math at positionMs=0.
            double shiftMs = layer.GetShiftMs();
            double headSkipMs = Math.Max(0, -shiftMs);
            double effectiveTrimStart = layer.TrimStartMs + headSkipMs + Math.Max(0, 0 - Math.Max(0, shiftMs));
            double residualHoldMs = Math.Max(0, shiftMs - 0);
            using var previewFrameSource = new VideoFrameStreamSource(path, 64, 64, fps: 10, residualHoldMs, trimStartMs: effectiveTrimStart, trimEndMs: layer.TrimEndMs);
            var previewFrame = previewFrameSource.GetNextFrame();

            // Export path: ExportEngine.CreateFrameSource passes GetShiftMs() straight through.
            using var exportFrameSource = new VideoFrameStreamSource(path, 64, 64, fps: 10, shiftMs, trimStartMs: layer.TrimStartMs, trimEndMs: layer.TrimEndMs);
            var exportFrame = exportFrameSource.GetNextFrame();

            var previewPixel = previewFrame.GetPixel(32, 32);
            var exportPixel = exportFrame.GetPixel(32, 32);

            // Both should show "late" (blue) content -- the skipped-ahead 200ms+ portion.
            Assert.True(exportPixel.Blue > 150, $"Expected export frame 0 to show late (blue) content, got {exportPixel}.");
            Assert.True(previewPixel.Blue > 150, $"Expected preview frame at position 0 to show late (blue) content, got {previewPixel}.");
            Assert.Equal(exportPixel.Red, previewPixel.Red);
            Assert.Equal(exportPixel.Blue, previewPixel.Blue);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    [Fact]
    public void SetLayers_ComputesDurationFromLongestLayer()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10);
            engine.SetLayers(layers.Layers);

            Assert.InRange(engine.DurationMs, 1800, 2200);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    [Fact]
    public void Seek_WhilePaused_RaisesOneFrameAtRequestedPosition()
    {
        var (path, tempDir) = CreateFixtureClip("blue", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10);
            engine.SetLayers(layers.Layers);

            SKBitmap? received = null;
            engine.FrameReady += bmp => received = bmp;

            engine.Seek(1000);

            Assert.False(engine.IsPlaying);
            Assert.Equal(1000, engine.PositionMs);
            Assert.NotNull(received);
            received!.Dispose();
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    [Fact]
    public void PlayThenStop_ReachesEndAndStopsOnItsOwn()
    {
        var (path, tempDir) = CreateFixtureClip("green", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: new FakeAudioSink());
            engine.SetLayers(layers.Layers);

            int frameCount = 0;
            var stopped = new ManualResetEventSlim(false);
            engine.FrameReady += bmp => { frameCount++; bmp.Dispose(); };
            engine.PlaybackStopped += () => stopped.Set();

            engine.Play();
            bool finished = stopped.Wait(TimeSpan.FromSeconds(10));

            Assert.True(finished, "Playback did not stop on its own within the timeout.");
            Assert.False(engine.IsPlaying);
            Assert.True(frameCount > 0, "Expected at least one frame to be rendered.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }
}
