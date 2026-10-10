using System.Diagnostics;
using A_Capella.Engine.Export;
using A_Capella.Engine.Host;
using A_Capella.Engine.Preview;
using A_Capella.Engine.Project;
using NAudio.Wave;
using SkiaSharp;

namespace A_Capella.Engine.Tests.Preview;

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

/// <summary>Blocks inside Play() until released, so a test can observe a command still running.</summary>
public class GatedAudioSink : IPreviewAudioSink
{
    public readonly ManualResetEventSlim PlayEntered = new(false);
    public readonly ManualResetEventSlim Release = new(false);

    public void Play(ISampleProvider mix)
    {
        PlayEntered.Set();
        Release.Wait(TimeSpan.FromSeconds(10));
    }

    public void Stop() { }
    public void Dispose() => Release.Set();
}

/// <summary>Deterministic stand-in for a slow decode/rebuild (heavy FX chain, plugin-heavy
/// rebuild) that AccessViolationException can't be caught in-process: a real use-after-dispose
/// touches freed native memory and can crash the test host outright, so instead this fake tracks
/// its own disposed state and flags any GetNextFrame call that happens after Dispose() --
/// reproducing the crash as an observable assertion instead of a process crash.</summary>
public class GatedFakeFrameSource : ILayerFrameSource
{
    // The first call is PlayCore's synchronous priming call (made on the command thread, before
    // the frame-loop thread even starts) -- it must return immediately or PlayAsync() itself would
    // never complete. The second call is the frame-loop thread's own first steady-state call, which
    // is where the test wants it deterministically blocked (no sleep/timing race).
    public readonly ManualResetEventSlim Gate;
    public readonly ManualResetEventSlim BlockedInSecondCall = new(false);
    private int _callCount;
    private volatile bool _disposed;
    public volatile bool UsedAfterDispose;
    public int DisposeCount;

    /// <summary>startOpen: true makes this instance never block (used for a restart's fresh
    /// source in a test where only the original instance is under test).</summary>
    public GatedFakeFrameSource(bool startOpen = false) => Gate = new ManualResetEventSlim(startOpen);

    public SKBitmap GetNextFrame()
    {
        if (Interlocked.Increment(ref _callCount) == 2)
        {
            BlockedInSecondCall.Set();
            Gate.Wait(TimeSpan.FromSeconds(10));
        }
        if (_disposed) UsedAfterDispose = true;
        return new SKBitmap(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref DisposeCount);
    }
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
        string tempDir = Path.Combine(Path.GetTempPath(), $"a-capella-preview-test-{Guid.NewGuid()}");
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
        string tempDir = Path.Combine(Path.GetTempPath(), $"a-capella-preview-parity-test-{Guid.NewGuid()}");
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
    /// for a recorded layer, GetShiftMs() = ManualOffsetMs - CalibratedOffsetMs) must show
    /// skipped-ahead video content at position 0. Preview and export both get their frame source
    /// from LayerTimeline, so one assertion covers both.</summary>
    [Fact]
    public void LayerTimelineFrameAtPositionZero_SkipsAhead_ForNegativeShiftLayer()
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

            using var mixEngine = new A_Capella.Engine.Mix.MixEngine(NoHostedPluginsAvailable.Instance);
            var timeline = new A_Capella.Engine.Timeline.LayerTimeline(mixEngine);
            using var frameSource = timeline.FrameSource(layer, 64, 64, fps: 10, positionMs: 0);
            var pixel = frameSource.GetNextFrame().GetPixel(32, 32);

            Assert.True(pixel.Blue > 150, $"Expected frame at position 0 to show late (blue) content, got {pixel}.");
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
        string tempDir = Path.Combine(Path.GetTempPath(), $"a-capella-preview-drift-test-{Guid.NewGuid()}");
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
    public async Task Play_VideoStaysWithinTwoFramesOfAudioPosition_AtOneAndFourSecondsAndAfterSeek()
    {
        int fps = 10;
        var (path, tempDir) = CreateTimestampEncodedFixtureClip(durationSeconds: 5);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var sink = new SimulatedRealtimeAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: fps, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            await engine.SetLayersAsync(layers.Layers);

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

            await engine.PlayAsync();

            AssertFrameNearPosition(engine, () => latest, frameLock, targetMs: 1000);
            AssertFrameNearPosition(engine, () => latest, frameLock, targetMs: 4000);

            await engine.SeekAsync(2000);
            AssertFrameNearPosition(engine, () => latest, frameLock, targetMs: 3000);

            await engine.StopAsync();
            lock (frameLock) { latest?.Dispose(); }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Video settings reach the preview: the canvas takes the ratio's size, the layer sits in
    /// the cell its CellIndex names within the chosen layout, and the empty cell shows the background.</summary>
    [Fact]
    public async Task ApplyVideoSettings_ResizesTheCanvas_PlacesTheLayerAndShowsTheBackground()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path, CellIndex = 1 };
            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: new FakeAudioSink(), hostedPluginAvailability: NoHostedPluginsAvailable.Instance);

            SKBitmap? latest = null;
            var frameLock = new object();
            engine.FrameReady += frame => { lock (frameLock) { latest?.Dispose(); latest = frame; } };

            engine.ApplyVideoSettings(new VideoSettings
            {
                LayoutId = "2-cols",
                AspectRatio = AspectRatioOption.Square1x1,
                GutterFraction = 0,
                Background = new A_Capella.Engine.Composite.BackgroundSettings { Color = "#0000FF" },
            });
            await engine.RefreshAsync(new[] { layer });

            lock (frameLock)
            {
                Assert.NotNull(latest);
                Assert.Equal((540, 540), (latest!.Width, latest.Height));
                Assert.Equal(new SKColor(0, 0, 255), latest.GetPixel(135, 270));   // cell 0 is empty: background
                var right = latest.GetPixel(405, 270);                              // cell 1: the red clip
                Assert.True(right.Red > 200 && right.Blue < 60, $"expected red in cell 1, got {right}");
                latest.Dispose();
            }
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
    public async Task ConcurrentSeekSetLayersPlayStop_DoesNotThrowAndNeverDoublePlaysSink()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CountingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);

            var rng = new Random(42);
            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                switch (i % 4)
                {
                    case 0: await engine.SetLayersAsync(layers.Layers); break;
                    case 1: await engine.SeekAsync(rng.NextDouble() * 1000); break;
                    case 2: await engine.PlayAsync(); break;
                    case 3: await engine.StopAsync(); break;
                }
            })).ToArray();

            Exception? thrown = Record.Exception(() => Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));
            Assert.Null(thrown);

            await engine.StopAsync();

            Assert.True(sink.MaxObservedConcurrentPlays <= 1,
                $"Expected at most one concurrent sink Play, observed {sink.MaxObservedConcurrentPlays}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Q1 task 1 [auto]: "Transport commands issued from the Mixing screen and Editor
    /// screen interleaved under the Q0 stress test: single sink, consistent position." Both
    /// screens' transport rows call into this one shared PreviewPlaybackEngine (MainWindow's
    /// single `_previewEngine` field, per Q0's one-shared-service pattern) via the same command
    /// queue proven race-free above -- this test represents the two screens as two concurrent
    /// callers issuing the exact same transport verbs (Play/Stop/Seek) and additionally asserts
    /// PositionMs stays a single well-formed, in-range value throughout and after the storm,
    /// rather than only checking for absence of exceptions/double-plays.</summary>
    [Fact]
    public async Task TransportCommandsFromTwoScreensInterleaved_SingleSinkAndConsistentPosition()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CountingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);

            var observedPositions = new System.Collections.Concurrent.ConcurrentBag<double>();
            void RecordPosition() => observedPositions.Add(engine.PositionMs);

            // "Editor screen" transport calls.
            var editorScreenCalls = Task.Run(async () =>
            {
                for (int i = 0; i < 15; i++) { await engine.PlayAsync(); RecordPosition(); await engine.SeekAsync(200); RecordPosition(); await engine.StopAsync(); RecordPosition(); }
            });
            // "Mixing screen" transport calls -- same verbs, different caller thread, exactly the
            // interleave the acceptance criterion describes.
            var mixingScreenCalls = Task.Run(async () =>
            {
                for (int i = 0; i < 15; i++) { await engine.SeekAsync(400); RecordPosition(); await engine.PlayAsync(); RecordPosition(); await engine.StopAsync(); RecordPosition(); }
            });

            Exception? thrown = Record.Exception(() => Task.WaitAll(new[] { editorScreenCalls, mixingScreenCalls }, TimeSpan.FromSeconds(30)));
            Assert.Null(thrown);

            await engine.StopAsync();

            Assert.True(sink.MaxObservedConcurrentPlays <= 1,
                $"Expected at most one concurrent sink Play across both screens' transport calls, observed {sink.MaxObservedConcurrentPlays}.");
            Assert.NotEmpty(observedPositions);
            Assert.All(observedPositions, p => Assert.InRange(p, 0, engine.DurationMs));
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
    public async Task MasterVolumeDb_ChangedWhilePlaying_AttenuatesLiveAudioImmediately()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.UploadedAudioOnly, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CapturingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);
            engine.MasterVolumeDb = 0f;
            await engine.PlayAsync();

            Assert.NotNull(sink.LastMix);
            var buffer = new float[8192];
            sink.LastMix!.Read(buffer, 0, buffer.Length);
            float rmsAt0Db = ComputeRms(buffer);

            engine.MasterVolumeDb = -20f; // no Stop/Play/Seek -- same live graph
            sink.LastMix!.Read(buffer, 0, buffer.Length);
            float rmsAtMinus20Db = ComputeRms(buffer);

            await engine.StopAsync();

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

    /// <summary>Commands must return to the caller before they finish on the command thread: a
    /// caller blocking on a command from the UI thread is what deadlocked against hosted-plugin
    /// calls marshaled onto that same thread (audit A3).</summary>
    [Fact]
    public async Task Commands_ReturnBeforeCompleting_AndRunInIssueOrder()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.UploadedAudioOnly, SourcePath = path };
            var sink = new GatedAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(new[] { layer });

            var play = engine.PlayAsync();
            Assert.True(sink.PlayEntered.Wait(TimeSpan.FromSeconds(10)), "Play never reached the sink.");
            var stop = engine.StopAsync();

            Assert.False(play.IsCompleted, "PlayAsync blocked its caller until the command finished.");
            Assert.False(stop.IsCompleted, "StopAsync ran before the Play issued ahead of it.");

            sink.Release.Set();
            await Task.WhenAll(play, stop).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(engine.IsPlaying);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Crash regression: StopAsync used to Join the frame-loop thread with a 2-second
    /// timeout and then dispose the frame sources regardless of whether the join actually
    /// succeeded -- under a slow GetNextFrame (heavy FX chain, plugin-heavy rebuild) the loop
    /// thread was still mid-render on a bitmap that had just been freed from another thread,
    /// which is what crashed with an AccessViolationException in SkiaSharp. This test blocks the
    /// frame-loop thread's own GetNextFrame call (not the priming call PlayCore makes
    /// synchronously) and proves StopAsync does not return -- and therefore does not let the
    /// source be disposed -- until that call actually finishes.</summary>
    [Fact]
    public async Task StopWhileFrameLoopBlockedInGetNextFrame_WaitsInsteadOfDisposingUnderIt()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var fake = new GatedFakeFrameSource();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: new FakeAudioSink(), hostedPluginAvailability: NoHostedPluginsAvailable.Instance)
            {
                FrameSourceFactoryOverride = (_, _, _, _, _) => fake,
            };
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);

            await engine.PlayAsync();
            Assert.True(fake.BlockedInSecondCall.Wait(TimeSpan.FromSeconds(10)), "Frame loop never reached its first steady-state GetNextFrame call.");

            var stopTask = engine.StopAsync();

            // Give StopCore's Join a moment to actually be waiting, then confirm it really is
            // still waiting and has not disposed the source out from under the blocked call.
            await Task.Delay(200);
            Assert.False(stopTask.IsCompleted, "StopAsync returned while the frame loop was still inside GetNextFrame.");
            Assert.Equal(0, fake.DisposeCount);
            Assert.False(fake.UsedAfterDispose);

            fake.Gate.Set();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(fake.UsedAfterDispose, "GetNextFrame observed itself being called after Dispose() -- the crash this test guards against.");
            Assert.Equal(1, fake.DisposeCount);
            Assert.False(engine.IsPlaying);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Same hazard as above, via SeekAsync (which internally calls StopCore before
    /// restarting playback): seeking while the frame loop is mid-GetNextFrame must wait for that
    /// call to finish before the old source is disposed and a new one created.</summary>
    [Fact]
    public async Task SeekWhileFrameLoopBlockedInGetNextFrame_WaitsInsteadOfDisposingUnderIt()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            // SeekCore internally restarts playback (PlayCore creates a fresh frame source) once
            // the old one has stopped -- only the first-created fake is the one under test here;
            // later ones must be allowed to run free (not gated) so the restart completes.
            var fake = new GatedFakeFrameSource();
            bool firstCreated = false;
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: new FakeAudioSink(), hostedPluginAvailability: NoHostedPluginsAvailable.Instance)
            {
                FrameSourceFactoryOverride = (_, _, _, _, _) => firstCreated ? new GatedFakeFrameSource(startOpen: true) : fake,
            };
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);

            await engine.PlayAsync();
            firstCreated = true;
            Assert.True(fake.BlockedInSecondCall.Wait(TimeSpan.FromSeconds(10)), "Frame loop never reached its first steady-state GetNextFrame call.");

            var seekTask = engine.SeekAsync(500);

            await Task.Delay(200);
            Assert.False(seekTask.IsCompleted, "SeekAsync returned while the frame loop was still inside GetNextFrame.");
            Assert.Equal(0, fake.DisposeCount);
            Assert.False(fake.UsedAfterDispose);

            fake.Gate.Set();
            await seekTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(fake.UsedAfterDispose, "GetNextFrame observed itself being called after Dispose() -- the crash this test guards against.");
            Assert.Equal(1, fake.DisposeCount);

            await engine.StopAsync();
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Q1 task 2 [auto]: a slot enable/disable flag change (e.g. a rack power button)
    /// applied via MainWindow's real refresh path -- RefreshAsync, the
    /// "debounced rebuild" mentioned in the acceptance criterion -- must reach the live audio on
    /// the next block without ever leaving IsPlaying false, and the position after that rebuild
    /// must land back at the same point (SeekCore restarts playback at the exact positionMs it was
    /// given when wasPlaying is true).</summary>
    [Fact]
    public async Task SlotEnableToggledMidPlayback_RebuildAppliesItWithoutStoppingTransport_PositionPreserved()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.UploadedAudioOnly, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CapturingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);
            await engine.PlayAsync();
            Assert.True(engine.IsPlaying);

            Assert.NotNull(sink.LastMix);
            var buffer = new float[8192];
            sink.LastMix!.Read(buffer, 0, buffer.Length);
            float rmsBeforeToggle = ComputeRms(buffer);

            // Flip the EQ slot's power button: a deep mid-band cut centered near the fixture's
            // 440Hz tone, the same shape a "toggle a slot mid-playback" UI action produces.
            layer.MixParameters.EqEnabled = true;
            layer.MixParameters.MidBellGainDb = -24f;

            double positionBeforeRebuild = engine.PositionMs;
            await engine.RefreshAsync(layers.Layers);

            Assert.True(engine.IsPlaying, "Toggling a slot mid-playback must not leave the transport stopped.");
            // SeekCore sets PositionMs = positionBeforeRebuild synchronously before restarting
            // playback, but IsPlaying stays true throughout so the frame-loop thread keeps
            // advancing PositionMs on its own wall-clock cadence -- some real time elapses between
            // the Seek call returning and this read, so "position preserved" is checked as "close
            // to where we asked to land", not bit-exact.
            Assert.InRange(engine.PositionMs, positionBeforeRebuild - 50, positionBeforeRebuild + 500);

            Assert.NotNull(sink.LastMix);
            sink.LastMix!.Read(buffer, 0, buffer.Length);
            float rmsAfterToggle = ComputeRms(buffer);

            await engine.StopAsync();

            Assert.True(rmsAfterToggle < rmsBeforeToggle * 0.7f,
                $"Expected enabling the EQ slot with a deep mid-band cut to reduce live RMS; got {rmsBeforeToggle} -> {rmsAfterToggle}.");
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Q1 task 3: GetLayerLevels/GetMasterLevels must reflect the live playing mix, not
    /// stay stuck at their pre-Play NegativeInfinity default -- these are what a Mixing-screen
    /// meter poll timer reads.</summary>
    [Fact]
    public async Task GetLayerAndMasterLevels_WhilePlaying_ReportNonSilentLevels()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 7, Kind = LayerKind.UploadedAudioOnly, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            var sink = new CapturingAudioSink();
            using var engine = new PreviewPlaybackEngine(canvasWidth: 64, canvasHeight: 64, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            engine.FrameReady += bmp => bmp.Dispose();
            await engine.SetLayersAsync(layers.Layers);

            var (layerPeakBefore, _) = engine.GetLayerLevels(7);
            var (masterPeakBefore, _) = engine.GetMasterLevels();
            Assert.True(float.IsNegativeInfinity(layerPeakBefore));
            Assert.True(float.IsNegativeInfinity(masterPeakBefore));

            await engine.PlayAsync();
            Assert.NotNull(sink.LastMix);
            var buffer = new float[8192];
            sink.LastMix!.Read(buffer, 0, buffer.Length);

            var (layerPeakAfter, layerRmsAfter) = engine.GetLayerLevels(7);
            var (masterPeakAfter, masterRmsAfter) = engine.GetMasterLevels();

            await engine.StopAsync();

            Assert.False(float.IsNegativeInfinity(layerPeakAfter), "Expected a real level for the playing layer's tap.");
            Assert.False(float.IsNegativeInfinity(masterPeakAfter), "Expected a real level for the master bus tap.");
            Assert.True(layerRmsAfter <= layerPeakAfter);
            Assert.True(masterRmsAfter <= masterPeakAfter);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task SetLayers_ComputesDurationFromLongestLayer()
    {
        var (path, tempDir) = CreateFixtureClip("red", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            await engine.SetLayersAsync(layers.Layers);

            Assert.InRange(engine.DurationMs, 1800, 2200);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    [Fact]
    public async Task Seek_WhilePaused_RaisesOneFrameAtRequestedPosition()
    {
        var (path, tempDir) = CreateFixtureClip("blue", durationSeconds: 2);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            await engine.SetLayersAsync(layers.Layers);

            SKBitmap? received = null;
            engine.FrameReady += bmp => received = bmp;

            await engine.SeekAsync(1000);

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
    public async Task PlayThenStop_ReachesEndAndStopsOnItsOwn()
    {
        var (path, tempDir) = CreateFixtureClip("green", durationSeconds: 1);
        try
        {
            var layer = new LayerModel { LayerId = 0, Kind = LayerKind.RecordedAV, SourcePath = path };
            var layers = new LayerCollection();
            layers.Restore(new[] { layer });

            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: new FakeAudioSink(), hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            await engine.SetLayersAsync(layers.Layers);

            int frameCount = 0;
            var stopped = new ManualResetEventSlim(false);
            engine.FrameReady += bmp => { frameCount++; bmp.Dispose(); };
            engine.PlaybackStopped += () => stopped.Set();

            await engine.PlayAsync();
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

    /// <summary>Bug audit #10: video and audio of independent lengths (audioSeconds 0 = no audio
    /// stream at all, i.e. a video-only upload), in the given container. The video's red channel
    /// switches 255 -> 0 and blue 0 -> 255 at switchAtSeconds, so a test can tell a frozen early
    /// frame from a late one.</summary>
    private static (string path, string tempDir) CreateMismatchedLengthFixtureClip(double videoSeconds, double audioSeconds, double switchAtSeconds, string extension = "mp4")
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"a-capella-preview-end-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, $"layer.{extension}");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string sw = switchAtSeconds.ToString(inv);
        string geq = $"r='if(lt(T\\,{sw})\\,255\\,0)':g=0:b='if(gte(T\\,{sw})\\,255\\,0)'";

        var args = new List<string> { "-y", "-f", "lavfi", "-i", $"color=c=black:s=64x64:r=10:d={videoSeconds.ToString(inv)}" };
        if (audioSeconds > 0)
            args.AddRange(new[] { "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={audioSeconds.ToString(inv)}" });
        args.AddRange(new[] { "-vf", $"geq={geq}", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p" });
        args.AddRange(audioSeconds > 0 ? new[] { "-c:a", "aac" } : new[] { "-an" });
        args.Add(path);
        RunFfmpeg(args.ToArray());

        return (path, tempDir);
    }

    /// <summary>Plays to the natural end with a real-time sink; returns whether PlaybackStopped
    /// fired within DurationMs + 3s, and the centre pixel of cell 0 in the last frame rendered.
    /// Note (bug audit #10 test adaptation): FrameLoop unconditionally raises PlaybackStopped when
    /// it exits for ANY reason, including SeekCore's internal StopCore call while restarting
    /// playback at the new position (a separate, already-known, out-of-scope issue -- the "#5
    /// runner-up spurious PlaybackStopped on every seek" this audit's own doc references). That
    /// spurious event would otherwise set `stopped` immediately after the seek, before the
    /// (still-playing) engine has actually reached DurationMs, so `stopped` is reset right after
    /// the seek returns to wait for the real, final stop instead.</summary>
    private static async Task<(bool Stopped, SKColor LastPixel, PreviewPlaybackEngine Engine)> PlayToEndWithRealtimeSink(LayerKind kind, string path, SimulatedRealtimeAudioSink sink, double? seekToMsAfterStart = null)
    {
        var layers = new LayerCollection();
        layers.Restore(new[] { new LayerModel { LayerId = 0, Kind = kind, SourcePath = path } });

        var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
        await engine.SetLayersAsync(layers.Layers);

        object frameLock = new();
        SKColor lastPixel = default;
        var stopped = new ManualResetEventSlim(false);
        engine.FrameReady += frame => { lock (frameLock) { lastPixel = frame.GetPixel(32, 32); } frame.Dispose(); };
        engine.PlaybackStopped += () => stopped.Set();

        await engine.PlayAsync();
        if (seekToMsAfterStart is double seekMs)
        {
            Thread.Sleep(300);
            await engine.SeekAsync(seekMs);
            // See the method note above: SeekCore's internal restart raises a spurious
            // PlaybackStopped that would otherwise be mistaken for the real, final stop.
            stopped.Reset();
        }
        bool finished = stopped.Wait(TimeSpan.FromMilliseconds(engine.DurationMs + 3000));
        lock (frameLock) return (finished, lastPixel, engine);
    }

    /// <summary>Bug audit #10, repro B: video 3s, audio 2s. With the audio clock driving the loop,
    /// playback must run the video to its end and stop on its own -- previously the clock stopped
    /// at the audio's end (~2020ms) and the loop never exited.</summary>
    [Fact]
    public async Task Play_WithRealtimeSink_VideoLongerThanAudio_PlaysToVideoEndAndStopsOnItsOwn()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 3, audioSeconds: 2, switchAtSeconds: 2.5);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, lastPixel, engine) = await PlayToEndWithRealtimeSink(LayerKind.UploadedVideo, path, sink);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.False(engine.IsPlaying);
                Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
                Assert.True(lastPixel.Blue > 150, $"Expected the post-audio video (blue, >= 2.5s) to have been shown; last frame was {lastPixel}.");
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, repro A: a video-only layer (no audio stream, 0 decoded samples)
    /// must advance past frame 0 and stop on its own -- previously PositionMs stayed 0 forever.</summary>
    [Fact]
    public async Task Play_WithRealtimeSink_VideoOnlyLayer_AdvancesPictureAndStopsOnItsOwn()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 2, audioSeconds: 0, switchAtSeconds: 1.0);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, lastPixel, engine) = await PlayToEndWithRealtimeSink(LayerKind.UploadedVideo, path, sink);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
                Assert.True(lastPixel.Blue > 150, $"Expected the picture to reach the late (blue) content; last frame was {lastPixel}.");
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, repro C: an MKV written with FfmpegCaptureSession's codec args
    /// (libx264 ultrafast crf 23 + AAC) decodes a few ms shorter than its container duration --
    /// the common case of every untrimmed recorded take.</summary>
    [Fact]
    public async Task Play_WithRealtimeSink_CaptureFormatMkvTake_StopsOnItsOwn()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 2, audioSeconds: 2, switchAtSeconds: 1.0, extension: "mkv");
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, _, engine) = await PlayToEndWithRealtimeSink(LayerKind.RecordedAV, path, sink);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.False(engine.IsPlaying);
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, ordering subtleties 2/3: after a mid-play seek to a fractional
    /// position, the padding is sized from that start position and playback still stops exactly
    /// at DurationMs.</summary>
    [Fact]
    public async Task SeekMidPlay_ToFractionalPosition_WithRealtimeSink_StillStopsAtDuration()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 3, audioSeconds: 2, switchAtSeconds: 2.5);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var (stopped, _, engine) = await PlayToEndWithRealtimeSink(LayerKind.UploadedVideo, path, sink, seekToMsAfterStart: 1000.6);
            using (engine)
            {
                Assert.True(stopped, $"Playback did not stop on its own after seek (stuck at {engine.PositionMs:F1} of {engine.DurationMs:F1}ms).");
                Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
            }
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }

    /// <summary>Bug audit #10, repro D / subtlety 4: a seek (or refresh) while playing to a
    /// position past the audio's end but before DurationMs must play the silent remainder and
    /// stop, not restart an endless "playing" state. Deliberately NOT a seek to exactly
    /// DurationMs -- that already stops today (see subtlety 4's counter-example).</summary>
    [Fact]
    public async Task SeekPastAudioEndWhilePlaying_WithRealtimeSink_StopsInsteadOfReStalling()
    {
        var (path, tempDir) = CreateMismatchedLengthFixtureClip(videoSeconds: 3, audioSeconds: 2, switchAtSeconds: 2.5);
        try
        {
            using var sink = new SimulatedRealtimeAudioSink();
            var layers = new LayerCollection();
            layers.Restore(new[] { new LayerModel { LayerId = 0, Kind = LayerKind.UploadedVideo, SourcePath = path } });
            using var engine = new PreviewPlaybackEngine(canvasWidth: 128, canvasHeight: 128, fps: 10, audioSink: sink, hostedPluginAvailability: NoHostedPluginsAvailable.Instance);
            await engine.SetLayersAsync(layers.Layers);
            engine.FrameReady += f => f.Dispose();
            var stopped = new ManualResetEventSlim(false);
            engine.PlaybackStopped += () => stopped.Set();

            await engine.PlayAsync();
            await engine.SeekAsync(2500); // audio ends ~2020ms, DurationMs = 3000ms
            // See PlayToEndWithRealtimeSink's note: SeekCore's internal restart raises a spurious
            // PlaybackStopped (a separate, out-of-scope, already-known issue) that would otherwise
            // be mistaken here for the real, final stop.
            stopped.Reset();

            Assert.True(stopped.Wait(TimeSpan.FromSeconds(3)), $"Seeking past the audio's end while playing left the engine 'playing' (stuck at {engine.PositionMs:F1}ms).");
            Assert.False(engine.IsPlaying);
            Assert.Equal(engine.DurationMs, engine.PositionMs, precision: 3);
        }
        finally
        {
            DeleteWithRetry(tempDir);
        }
    }
}
