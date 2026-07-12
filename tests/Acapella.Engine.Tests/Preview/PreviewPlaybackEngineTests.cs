using System.Diagnostics;
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
            Directory.Delete(tempDir, recursive: true);
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
            Directory.Delete(tempDir, recursive: true);
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
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
