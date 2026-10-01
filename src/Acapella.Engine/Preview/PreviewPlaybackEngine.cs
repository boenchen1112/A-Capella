using Acapella.Engine.Composite;
using Acapella.Engine.Export;
using Acapella.Engine.Host;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
using Acapella.Engine.Timeline;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SkiaSharp;

namespace Acapella.Engine.Preview;

/// <summary>Plays a built mix through some audio output. Abstracted so tests can exercise the
/// frame-loop/lifecycle logic without opening a real WASAPI device (this suite runs every
/// session and stays hardware-independent by convention -- see tools/HardwareChecks for the
/// project's actual hardware-in-the-loop checks).</summary>
public interface IPreviewAudioSink : IDisposable
{
    void Play(ISampleProvider mix);
    void Stop();

    /// <summary>True if this sink actually pulls samples from the stream passed to Play() at
    /// real-time pace (a real audio device, or a test sink that simulates one). When true, a
    /// PositionTrackingSampleProvider wrapped around that stream reports genuine elapsed audio
    /// time and PreviewPlaybackEngine slaves its frame clock to it (audit A2). Defaults to false
    /// so a no-op fake (nothing ever pulls the stream) falls back to a wall-clock stopwatch
    /// instead of a position that would never advance.</summary>
    bool DrivesRealtime => false;
}

/// <summary>Default sink: the system's default render device via WasapiOut.</summary>
public class WasapiPreviewAudioSink : IPreviewAudioSink
{
    private WasapiOut? _output;
    private readonly string? _deviceId;

    public WasapiPreviewAudioSink(string? deviceId = null) => _deviceId = deviceId;

    public bool DrivesRealtime => true;

    public void Play(ISampleProvider mix)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = _deviceId is not null
            ? enumerator.GetDevice(_deviceId)
            : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
        _output.Init(mix);
        _output.Play();
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Real-time synced audio+video playback of the composited project, for the Editor screen's
/// preview transport: Restart / Play-Stop / scrub, updating live as
/// trim/source/FX change. A throughput spike (see tools/HardwareChecks "previewspike") measured
/// ~230fps for on-the-fly 4-layer decode+composite at 320x240 cells -- well above the 30fps
/// playback target -- so this drives VideoFrameStreamSource + Compositor directly per frame
/// rather than pre-rendering a proxy file.
///
/// Audio and video are decoded fresh on every Play/Seek (not continuously re-decoded during
/// playback) since that mirrors the existing preview-mix rebuild pattern and stays well within
/// the measured throughput headroom for a discrete transport action.
///
/// All commands (SetLayers/Refresh/Play/Stop/Seek) are serialized through a single dedicated
/// command thread (audit B1) and return a task instead of blocking: callers (a debounced live
/// refresh, transport clicks, keyboard shortcuts) may issue them from any thread, including the UI
/// thread, and in any interleaving -- each command is atomic relative to the others, and commands
/// run in the order they were issued.
/// </summary>
public class PreviewPlaybackEngine : IDisposable
{
    private readonly MixEngine _mixEngine;
    private readonly LayerTimeline _timeline;
    private readonly int _sampleRate;
    private readonly int _fps;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private readonly IPreviewAudioSink _audioSink;

    private List<LayerModel> _layers = new();
    private List<ILayerFrameSource>? _frameSources;
    private Thread? _frameLoopThread;
    private volatile bool _stopRequested;

    /// <summary>Test seam only: when set, used instead of _timeline.FrameSource to create each
    /// layer's frame source, so a test can inject a fake (e.g. one that blocks GetNextFrame on a
    /// gate) without a real media file. Null in production.</summary>
    internal Func<LayerModel, int, int, int, double, ILayerFrameSource>? FrameSourceFactoryOverride;

    private volatile float _masterVolumeDb;
    // Live handle onto the currently-playing graph's master-volume node (audit: master volume
    // slider had no audible effect during active playback -- BuildMix only bakes masterVolumeDb
    // into the graph once, at StartAudio time). Not routed through the command queue: this is a
    // value push onto an already-built graph, not an operation that needs to be atomic relative to
    // SetLayers/Play/Stop/Seek. Null whenever no audio graph is currently live (stopped, or a
    // hardware-free test sink that never calls StartAudio).
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _liveMasterVolumeStage;

    /// <summary>Bus gain (audit P1 task 4). Setting this while playing updates the live graph
    /// immediately; StartAudio also reads it when building a fresh graph for the next Play/Seek.</summary>
    public float MasterVolumeDb
    {
        get => _masterVolumeDb;
        set
        {
            _masterVolumeDb = value;
            var stage = _liveMasterVolumeStage;
            if (stage is not null) stage.Volume = DbToLinear(value);
        }
    }

    private static float DbToLinear(float db) => (float)Math.Pow(10, db / 20.0);

    // Single-threaded command queue (audit B1): every public operation below enqueues work here
    // instead of running inline, so SetLayers/Play/Stop/Seek from any caller thread never
    // interleave. Core methods call each other directly (never via Enqueue) to avoid a command
    // waiting on itself.
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _commandQueue = new();
    private readonly Thread _commandThread;

    // Set first thing in Dispose. A background pitch-render completion can land on a thread-pool
    // thread at any time, including while Dispose is already running on the UI thread -- this stops
    // its refresh from enqueueing (or running) a SeekCore/PlayCore that could try to marshal onto
    // that same blocked UI thread.
    private volatile bool _disposed;

    public event Action<SKBitmap>? FrameReady;
    public event Action? PlaybackStopped;

    /// <summary>Forwarded from MixEngine -- see its own PitchRenderStarted/Completed/Failed docs.
    /// A completed render that's still current also triggers a refresh at the current position here
    /// (see the constructor) so the corrected audio actually replaces the placeholder the next time
    /// this engine's chain is rebuilt; callers only need these for UI feedback (status bar), and
    /// PitchRenderCompleted's bool is only meaningful to a caller that cares whether a refresh
    /// happened -- counting Started against Completed/Failed for a pending-count indicator balances
    /// regardless of its value.</summary>
    public event Action<int>? PitchRenderStarted;
    public event Action<int, bool>? PitchRenderCompleted;
    public event Action<int, string>? PitchRenderFailed;

    public bool IsPlaying { get; private set; }
    public double PositionMs { get; private set; }
    public double DurationMs { get; private set; }

    /// <summary>Grid-identification overlay toggle (v5 P2 task 3), read at render time -- not
    /// routed through the command queue, same reasoning as MasterVolumeDb. Export never sets this;
    /// it always composites without labels regardless.</summary>
    public volatile bool ShowLayerLabels = true;

    /// <summary>hostedService, when given, is shared with MainWindow's launcher UI and
    /// ExportEngine (v7 Q0 task 1, audit A1) so a plugin edit is audible on the next rebuild and
    /// export captures the same live state -- pass hostedPluginAvailability instead only for
    /// isolated tests/callers that don't need that sharing.</summary>
    public PreviewPlaybackEngine(int canvasWidth = 640, int canvasHeight = 480, int fps = 30, int sampleRate = 44100, string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe", IPreviewAudioSink? audioSink = null, IHostedPluginAvailability? hostedPluginAvailability = null, HostedPluginService? hostedService = null)
    {
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _fps = fps;
        _sampleRate = sampleRate;
        _audioSink = audioSink ?? new WasapiPreviewAudioSink();
        _mixEngine = hostedService is not null ? new MixEngine(hostedService) : new MixEngine(hostedPluginAvailability);
        _timeline = new LayerTimeline(_mixEngine, ffmpegPath, ffprobePath);

        // Only the preview opts into background pitch rendering (see MixEngine.
        // NonBlockingPitchCorrection's doc) -- a chosen pitch mode, a trim, or closing Melodyne's
        // editor used to re-render the whole layer synchronously on the command thread, freezing
        // Play/Seek/Refresh for however long that took. Export keeps the blocking default.
        _mixEngine.NonBlockingPitchCorrection = true;
        _mixEngine.PitchRenderStarted += layerId => PitchRenderStarted?.Invoke(layerId);
        _mixEngine.PitchRenderCompleted += (layerId, stillCurrent) =>
        {
            PitchRenderCompleted?.Invoke(layerId, stillCurrent);
            if (!stillCurrent) return; // superseded -- nothing currently built needs refreshing
            // Re-run the existing refresh path at the current position: BuildLayerChain looks the
            // layer's pitch cache up again from scratch, so this just needs to happen. _disposed
            // guards against this landing while Dispose is mid-teardown: a refresh that reached
            // SeekCore during that window could call PlayCore, which can block-marshal hosted calls
            // onto the UI thread -- the same thread Dispose's StopAsync().GetAwaiter().GetResult()
            // below is about to block on without pumping.
            Enqueue(() => { if (!_disposed) SeekCore(PositionMs); });
        };
        _mixEngine.PitchRenderFailed += (layerId, message) => PitchRenderFailed?.Invoke(layerId, message);

        _commandThread = new Thread(RunCommandLoop) { IsBackground = true };
        _commandThread.Start();
    }

    private void RunCommandLoop()
    {
        foreach (var command in _commandQueue.GetConsumingEnumerable())
            command();
    }

    // Commands never block the caller: the returned task completes (or faults) on the command
    // thread. Chain builds on that thread block-marshal hosted-plugin calls onto the WPF UI thread
    // (audit A3), so a caller that waited synchronously from the UI thread would deadlock; awaiting
    // is safe from any thread. Continuations run asynchronously so an awaiter never executes on
    // the command thread itself.
    private Task Enqueue(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _commandQueue.Add(() =>
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding() has already run (Dispose in progress/done) -- a pitch-render
            // completion landing after that would otherwise throw on whatever thread raised it
            // (a background Task continuation, not the UI thread), instead of just being a no-op.
            done.SetCanceled();
        }
        return done.Task;
    }

    /// <summary>Rebinds the layer set this engine plays and recomputes DurationMs. Takes a
    /// snapshot of the list, so later edits to the caller's collection don't race playback.</summary>
    public Task SetLayersAsync(IEnumerable<LayerModel> layers)
    {
        var snapshot = layers.ToList();
        return Enqueue(() => SetLayersCore(snapshot));
    }

    /// <summary>Live refresh after sources/trim/FX change: rebinds the layers and re-renders at
    /// the current position as one atomic command, continuing playback if it was playing.</summary>
    public Task RefreshAsync(IEnumerable<LayerModel> layers)
    {
        var snapshot = layers.ToList();
        return Enqueue(() =>
        {
            SetLayersCore(snapshot);
            SeekCore(PositionMs);
        });
    }

    private void SetLayersCore(IReadOnlyList<LayerModel> layers)
    {
        _layers = LayerTimeline.InCellOrder(layers);
        DurationMs = _timeline.DurationMs(_layers, _sampleRate);
        PositionMs = Math.Min(PositionMs, DurationMs);
    }

    public Task PlayAsync() => Enqueue(PlayCore);

    private void PlayCore()
    {
        if (IsPlaying || _layers.Count == 0) return;
        // A loop that reached the end on its own may still be finishing its cleanup; wait for it so
        // its trailing PlaybackStopped can't land after this new playback has started.
        _frameLoopThread?.Join();
        _frameLoopThread = null;
        StartFrameSources(PositionMs);

        var sources = _frameSources!;
        // Prime every source's first frame before starting audio/clock (audit A2): decoding the
        // first frame of 4 freshly spawned ffmpeg processes can take hundreds of ms, and starting
        // the clock first meant the video was already behind by that amount before frame 1 of the
        // catch-up loop even ran.
        var lastFrames = new SKBitmap[sources.Count];
        var consumedFrames = new int[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            lastFrames[i] = sources[i].GetNextFrame();
            consumedFrames[i] = 1;
        }
        RenderComposite(lastFrames);

        double startPositionMs = PositionMs;
        var (positionTracker, padded) = StartAudio(startPositionMs);
        bool useAudioClock = _audioSink.DrivesRealtime;

        IsPlaying = true;
        _stopRequested = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        _frameLoopThread = new Thread(() => FrameLoop(clock, positionTracker, useAudioClock, startPositionMs, sources, lastFrames, consumedFrames, padded)) { IsBackground = true };
        _frameLoopThread.Start();
    }

    /// <summary>Chases a target frame index derived from the playback clock rather than assuming
    /// one loop iteration equals one frame (audit A1): any iteration slower than one frame
    /// interval (slow ffmpeg pipe read, UI marshaling, GC) used to permanently push video behind
    /// audio since nothing ever caught video back up. Here, each pass pulls (and discards) frames
    /// until the per-source consumed count reaches the clock-derived target, rendering only the
    /// last frame pulled -- so a slow iteration drops frames instead of falling behind forever.
    /// When a source is already caught up (or ahead, e.g. a shorter/frozen layer), no frame is
    /// pulled that pass and its last-known frame is reused.</summary>
    private void FrameLoop(System.Diagnostics.Stopwatch clock, PositionTrackingSampleProvider? positionTracker, bool useAudioClock, double startPositionMs, IReadOnlyList<ILayerFrameSource> sources, SKBitmap[] lastFrames, int[] consumedFrames, PadToLengthSampleProvider? padded)
    {
        try
        {
            while (!_stopRequested)
            {
                // Read the end flag before the position (subtlety 2): if the stream ended, the tracker
                // has counted every padded sample, so the project end has been reached.
                bool audioEnded = useAudioClock && padded is not null && padded.Ended;
                double elapsedMs = useAudioClock && positionTracker is not null
                    ? positionTracker.PositionMs
                    : clock.Elapsed.TotalMilliseconds;
                PositionMs = audioEnded ? DurationMs : Math.Min(startPositionMs + elapsedMs, DurationMs);
                int targetFrameIndex = (int)(elapsedMs / 1000.0 * _fps);

                for (int i = 0; i < sources.Count; i++)
                {
                    while (consumedFrames[i] <= targetFrameIndex)
                    {
                        lastFrames[i] = sources[i].GetNextFrame();
                        consumedFrames[i]++;
                    }
                }

                RenderComposite(lastFrames);

                if (PositionMs >= DurationMs) break;

                // Pacing comes from the clock-derived target frame index above, not sleep precision --
                // this tick just bounds CPU spin while waiting for the next frame boundary.
                Thread.Sleep(5);
            }
        }
        finally
        {
            // Single, explicit owner (crash fix): this loop thread -- and only this thread -- disposes
            // the sources it was handed, and only once it will never touch them again (loop body is
            // over, including the final RenderComposite pass). Previously StopCore disposed
            // _frameSources after a 2-second Join *regardless of whether the join timed out*, so under
            // a slow frame loop (heavy FX/plugin rebuild, slow GetNextFrame) this thread could still be
            // mid-RenderComposite on a bitmap StopCore had just freed from another thread --
            // SkiaSharp's native DrawBitmap then AccessViolationException'd on the freed handle.
            // Disposing here instead, unconditionally and exactly once, makes Stop/Seek/rebuild safe
            // to call no matter how slow this loop currently is: they only ever signal and wait.
            foreach (var s in sources) s.Dispose();
            _audioSink.Stop();
            _liveMasterVolumeStage = null;
            _frameSources = null;
            IsPlaying = false;
            // Unconditional, matching prior behavior: FrameLoop has always raised this on every exit
            // (natural end or a Stop/Seek-requested exit) -- SeekCore's internal restart relies on
            // that for its own bookkeeping elsewhere, and existing tests assert it.
            PlaybackStopped?.Invoke();
        }
    }

    private void RenderComposite(IReadOnlyList<SKBitmap> frames)
    {
        // Grid-gap compaction fix: key rects by each layer's actual CellIndex, not by position in
        // frames -- a lone layer at CellIndex 1 (cell 0 empty) must still land in the top-right
        // cell, not get compacted into top-left.
        var cellRects = Layout2x2Provider.GetCellRects(_canvasWidth, _canvasHeight, _layers.Take(frames.Count).Select(l => l.CellIndex).ToList());
        List<CellLabel?>? labels = null;
        if (ShowLayerLabels)
        {
            labels = _layers.Take(frames.Count)
                .Select(l => (CellLabel?)new CellLabel(LayerColorPalette.GetColor(l.CellIndex), l.Name is { Length: > 0 } n ? n : $"Layer {l.CellIndex + 1}"))
                .ToList();
        }
        var composited = Compositor.Composite(_canvasWidth, _canvasHeight, frames, cellRects, labels);
        FrameReady?.Invoke(composited);
    }

    private void RenderCurrentFrame(IReadOnlyList<ILayerFrameSource> sources)
    {
        var frames = sources.Select(s => s.GetNextFrame()).ToList();
        RenderComposite(frames);
    }

    private void StartFrameSources(double positionMs)
    {
        _frameSources = CreateFrameSources(positionMs);
    }

    private List<ILayerFrameSource> CreateFrameSources(double positionMs) =>
        _layers.Select(layer => FrameSourceFactoryOverride is not null
            ? FrameSourceFactoryOverride(layer, _canvasWidth / 2, _canvasHeight / 2, _fps, positionMs)
            : _timeline.FrameSource(layer, _canvasWidth / 2, _canvasHeight / 2, _fps, positionMs)).ToList();

    private (PositionTrackingSampleProvider Tracker, PadToLengthSampleProvider Padded) StartAudio(double positionMs)
    {
        const int sampleRate = 44100;
        var mixInputs = _layers.Select(l => _timeline.AudioInput(l, sampleRate)).ToList();

        var (mix, masterVolumeStage) = _mixEngine.BuildMixWithMasterVolumeHandle(mixInputs, sampleRate, MasterVolumeDb);
        _liveMasterVolumeStage = masterVolumeStage;
        ISampleProvider seeked = positionMs > 0
            ? new OffsetSampleProvider(mix) { SkipOver = TimeSpan.FromMilliseconds(positionMs) }
            : mix;

        // Bug audit #10: pad the post-seek stream with silence to the rest of the project's length
        // BEFORE the tracker counts it, so the audio clock reaches DurationMs even when every
        // layer's audio ends before its container does (video-only layer, video outlasting audio,
        // untrimmed MKV take). Must sit inside the tracker -- see ordering subtlety 1.
        var padded = new PadToLengthSampleProvider(seeked, DurationMs - positionMs);

        // Wraps the post-seek stream so samples actually pulled by the sink count from zero at
        // this playback's start position -- FrameLoop adds startPositionMs back on top (audit A2).
        var tracked = new PositionTrackingSampleProvider(padded);
        _audioSink.Play(tracked);
        return (tracked, padded);
    }

    public Task StopAsync() => Enqueue(StopCore);

    private void StopCore()
    {
        _stopRequested = true;
        // Wait for the loop to actually exit instead of a short timeout: the loop's own finally
        // block (see FrameLoop) is the sole owner of _frameSources/_lastFrame disposal, so this
        // must never return (and let a caller rebuild/replace layers) while that thread could
        // still be touching them. Safe to wait unboundedly here: this runs on the dedicated
        // command thread, never the UI thread, and FrameLoop never blocks on the command thread
        // or the UI thread (FrameReady is dispatched async -- see MainWindow), so there is no
        // cycle to deadlock on.
        _frameLoopThread?.Join();
        _frameLoopThread = null;

        // If nothing was ever playing (no frame loop thread existed), there's nothing for the
        // loop's finally to have cleaned up -- do it here so Stop is a no-op-safe idempotent call.
        if (!IsPlaying && _frameSources is null) return;
        _audioSink.Stop();
        _liveMasterVolumeStage = null;
        if (_frameSources is not null)
        {
            foreach (var s in _frameSources) s.Dispose();
            _frameSources = null;
        }
        IsPlaying = false;
    }

    public Task RestartAsync() => SeekAsync(0);

    /// <summary>Seeks to positionMs, clamped to [0, DurationMs]. If currently playing, restarts
    /// playback from the new position; if paused, renders a single static composited frame at that
    /// position so scrubbing while stopped still updates the preview. Atomic relative to concurrent
    /// Play/Stop/SetLayers calls (audit B1) since it runs entirely on the command thread.</summary>
    public Task SeekAsync(double positionMs) => Enqueue(() => SeekCore(positionMs));

    /// <summary>Relative seek, resolved against the position at the moment the command runs.</summary>
    public Task SeekByAsync(double deltaMs) => Enqueue(() => SeekCore(PositionMs + deltaMs));

    private void SeekCore(double positionMs)
    {
        positionMs = Math.Clamp(positionMs, 0, DurationMs);
        bool wasPlaying = IsPlaying;
        if (IsPlaying) StopCore();

        PositionMs = positionMs;

        if (wasPlaying)
        {
            PlayCore();
        }
        else if (_layers.Count > 0)
        {
            var sources = CreateFrameSources(positionMs);
            RenderCurrentFrame(sources);
            foreach (var s in sources) s.Dispose();
        }
    }

    /// <summary>Q1 task 3: peak/RMS in dBFS for one layer's post-slot-rack meter tap, straight
    /// from the live MixEngine this engine plays through (not the command queue -- MeterTap's
    /// fields are volatile, so this is a safe, cheap read from any thread, including a UI poll
    /// timer at ~30Hz).</summary>
    public (float PeakDb, float RmsDb) GetLayerLevels(int layerId) => _mixEngine.GetLayerLevels(layerId);

    /// <summary>Q1 task 3: peak/RMS in dBFS for the master bus meter tap.</summary>
    public (float PeakDb, float RmsDb) GetMasterLevels() => _mixEngine.GetMasterLevels();

    /// <summary>Tries to flip a slot's power toggle on the currently-playing chain in place,
    /// without a full rebuild -- not routed through the command queue, same reasoning as
    /// MasterVolumeDb: this is a value push onto an already-built graph's live node, not an
    /// operation that needs to be atomic relative to SetLayers/Play/Stop/Seek. Returns false when
    /// there's no live node to flip (see MixEngine.TrySetSlotEnabled), in which case the caller
    /// should fall back to a full refresh.</summary>
    public bool TrySetSlotEnabled(int layerId, string stage, bool enabled) => _mixEngine.TrySetSlotEnabled(layerId, stage, enabled);

    public void Dispose()
    {
        _disposed = true;
        // StopCore never marshals to the UI thread, so waiting here is deadlock-free.
        StopAsync().GetAwaiter().GetResult();
        _commandQueue.CompleteAdding();
        _commandThread.Join();
        _audioSink.Dispose();
        _mixEngine.Dispose();
    }
}
