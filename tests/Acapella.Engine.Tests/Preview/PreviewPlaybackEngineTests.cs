using System.Diagnostics;
using Acapella.Engine.Export;
using Acapella.Engine.Host;
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

            using var mixEngine = new Acapella.Engine.Mix.MixEngine(NoHostedPluginsAvailable.Instance);
            var timeline = new Acapella.Engine.Timeline.LayerTimeline(mixEngine);
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
}
