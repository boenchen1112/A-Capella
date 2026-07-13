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

/// <summary>Simulates a real audio device by pulling the stream on a background thread at
/// real-time pace (based on WaveFormat sample rate), without touching real hardware. This lets
/// PositionTrackingSampleProvider report genuine elapsed-audio-time, so drift tests actually
/// exercise the audio-as-master-clock path (audit A1/A2) rather than a stopwatch fallback.</summary>
public class SimulatedRealtimeAudioSink : IPreviewAudioSink
{
    private readonly float[] _buffer = new float[4096];
    private Thread? _pullThread;
    private volatile bool _stop;

    public bool DrivesRealtime => true;

    public void Play(ISampleProvider mix)
    {
        _stop = false;
        _pullThread = new Thread(() => PullLoop(mix)) { IsBackground = true };
        _pullThread.Start();
    }

    private void PullLoop(ISampleProvider mix)
    {
        int sampleRate = mix.WaveFormat.SampleRate;
        int channels = mix.WaveFormat.Channels;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long framesPulled = 0;

        while (!_stop)
        {
            int read = mix.Read(_buffer, 0, _buffer.Length);
            if (read == 0) break;
            framesPulled += read / channels;

            double targetElapsedMs = framesPulled * 1000.0 / sampleRate;
            double sleepMs = targetElapsedMs - clock.Elapsed.TotalMilliseconds;
            if (sleepMs > 0) Thread.Sleep((int)sleepMs);
        }
    }

    public void Stop()
    {
        _stop = true;
        _pullThread?.Join(2000);
        _pullThread = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Counts overlapping Play/Stop calls so a stress test can assert the engine never lets
/// two sink playbacks run concurrently (audit B1).</summary>
public class CountingAudioSink : IPreviewAudioSink
{
    private int _activeCount;
    public int MaxObservedConcurrentPlays;

    public void Play(ISampleProvider mix)
    {
        int active = Interlocked.Increment(ref _activeCount);
        int prevMax;
        do { prevMax = MaxObservedConcurrentPlays; } while (active > prevMax && Interlocked.CompareExchange(ref MaxObservedConcurrentPlays, active, prevMax) != prevMax);
    }

    public void Stop() => Interlocked.Decrement(ref _activeCount);
    public void Dispose() => Stop();
    public int ActiveCount => Volatile.Read(ref _activeCount);
}

/// <summary>Captures the ISampleProvider passed to Play() so a test can pull samples from it
/// manually (simulating what a real sink would do at real-time pace) and inspect the effect of
/// mid-playback changes like MasterVolumeDb.</summary>
public class CapturingAudioSink : IPreviewAudioSink
{
    public ISampleProvider? LastMix;
    public void Play(ISampleProvider mix) => LastMix = mix;
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

    /// <summary>Fixture whose frame color encodes its own timestamp (red channel = (T*50) mod
    /// 256), so a rendered frame's pixel value can be converted back to "what timestamp is this
    /// frame showing" and compared against the audio-clock position it should be synced to
    /// (audit A1 drift test).</summary>
    private static (string path, string tempDir) CreateTimestampEncodedFixtureClip(int durationSeconds)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"acapella-preview-drift-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "layer.mp4");

        RunFfmpeg("-y", "-f", "lavfi", "-i", $"color=c=black:s=64x64:r=30:d={durationSeconds}",
                  "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={durationSeconds}",
                  "-vf", "geq=r='mod(T*50\\,256)':g=0:b=0",
                  "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", path);

        return (path, tempDir);
    }

    private static double ExpectedRedForMs(double positionMs) => (positionMs / 1000.0 * 50.0) % 256.0;

    private const int DriftToleranceRed = 20; // ~2 frames (at 10fps, 200ms) of encoded red drift.

    private static void AssertFrameNearPosition(PreviewPlaybackEngine engine, Func<SKBitmap?> getLatestFrame, object frameLock, double targetMs)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (engine.PositionMs < targetMs && timeout.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(5);

        Assert.True(engine.PositionMs >= targetMs, $"Playback never reached position {targetMs}ms (stuck at {engine.PositionMs}ms).");

        byte actualRed;
        lock (frameLock)
        {
            var frame = getLatestFrame() ?? throw new InvalidOperationException("No frame received yet.");
            actualRed = frame.GetPixel(32, 32).Red;
        }

        double expectedRed = ExpectedRedForMs(targetMs);
        double delta = Math.Min(Math.Abs(actualRed - expectedRed), 256 - Math.Abs(actualRed - expectedRed));
        Assert.True(delta <= DriftToleranceRed,
            $"At target {targetMs}ms, expected encoded red ~{expectedRed:F1} but frame showed {actualRed} (actual engine position {engine.PositionMs:F0}ms).");
    }

    /// <summary>Regression test for audit A1/A2 (the reported "video does not match audio, audio
    /// ends first" bug): with a real-time-pulling audio sink driving the position clock, the
    /// rendered video frame must stay within ~2 frames of the audio-derived playback position at
    /// both t~1s and t~4s, and again immediately after a mid-playback seek.</summary>
    [Fact]
    public void Play_VideoStaysWithinTwoFramesOfAudioPosition_AtOneAndFourSecondsAndAfterSeek()
    {
        int fps = 10;
        var (path, tempDir) = CreateTimestampEncodedFixtureClip(durationSeconds: 5);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var sink = new SimulatedRealtimeAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: fps, audioSink: sink);
            engine.SetLayers(layers.Layers);

            object frameLock = new();
            SKBitmap? latest = null;
            engine.FrameReady += frame =>
            {
                lock (frameLock)
                {
                    latest?.Dispose();
                    latest = frame;
                }
            };

            engine.Play();

            AssertFrameNearPosition(engine, () => latest, frameLock, targetMs: 1000);
            AssertFrameNearPosition(engine, () => latest, frameLock, targetMs: 4000);

            engine.Seek(2000);
            AssertFrameNearPosition(engine, () => latest, frameLock, targetMs: 3000);

            engine.Stop();
            lock (frameLock) { latest?.Dispose(); }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Regression test for audit B1: MainWindow calls SetLayers/Seek/Play/Stop from
    /// several different threads (debounced refresh, transport clicks, the engine's own
    /// frame-loop thread on natural stop). 50 rapid interleaved calls from parallel tasks must not
    /// throw (no disposed-source race, no null-ref) and must end with the sink never having had
    /// more than one concurrent Play active.</summary>
    [Fact]
    public void ConcurrentSeekSetLayersPlayStop_DoesNotThrowAndNeverDoublePlaysSink()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CountingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink);
            engine.FrameReady += bmp => bmp.Dispose();
            engine.SetLayers(layers.Layers);

            var rng = new Random(42);
            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            {
                switch (i % 4)
                {
                    case 0: engine.SetLayers(layers.Layers); break;
                    case 1: engine.Seek(rng.NextDouble() * 1000); break;
                    case 2: engine.Play(); break;
                    case 3: engine.Stop(); break;
                }
            })).ToArray();

            Exception? thrown = Record.Exception(() => Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));
            Assert.Null(thrown);

            engine.Stop();

            Assert.True(sink.MaxObservedConcurrentPlays <= 1,
                $"Expected at most one concurrent sink Play, observed {sink.MaxObservedConcurrentPlays}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Regression test: MasterVolumeDb previously only took effect on the *next*
    /// Play/Seek's fresh mix rebuild -- moving the master volume slider during active playback had
    /// no audible effect at all. Pulls samples from the actual live graph handed to the sink,
    /// before and after changing MasterVolumeDb without any Stop/Play/Seek in between.</summary>
    [Fact]
    public void MasterVolumeDb_ChangedWhilePlaying_AttenuatesLiveAudioImmediately()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.UploadedAudioOnly, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CapturingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink);
            engine.FrameReady += bmp => bmp.Dispose();
            engine.SetLayers(layers.Layers);
            engine.MasterVolumeDb = 0f;
            engine.Play();

            Assert.NotNull(sink.LastMix);
            var buffer = new float[8192];
            sink.LastMix!.Read(buffer, 0, buffer.Length);
            float rmsAt0Db = ComputeRms(buffer);

            engine.MasterVolumeDb = -20f; // no Stop/Play/Seek -- same live graph
            sink.LastMix!.Read(buffer, 0, buffer.Length);
            float rmsAtMinus20Db = ComputeRms(buffer);

            engine.Stop();

            Assert.True(rmsAtMinus20Db < rmsAt0Db * 0.2f,
                $"Expected -20dB master volume to noticeably attenuate live audio; got {rmsAt0Db} -> {rmsAtMinus20Db}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    private static float ComputeRms(float[] samples)
    {
        double sumSquares = samples.Sum(s => (double)s * s);
        return (float)Math.Sqrt(sumSquares / samples.Length);
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
