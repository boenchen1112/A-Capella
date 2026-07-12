using Acapella.Engine.Composite;
using Acapella.Engine.Export;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;
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
/// preview transport (UI_Design_Spec v2): Restart / Play-Stop / scrub, updating live as
/// trim/source/FX change. A throughput spike (see tools/HardwareChecks "previewspike") measured
/// ~230fps for on-the-fly 4-layer decode+composite at 320x240 cells -- well above the 30fps
/// playback target -- so this drives VideoFrameStreamSource + Compositor directly per frame
/// rather than pre-rendering a proxy file.
///
/// Audio and video are decoded fresh on every Play/Seek (not continuously re-decoded during
/// playback) since that mirrors the existing preview-mix rebuild pattern and stays well within
/// the measured throughput headroom for a discrete transport action.
///
/// All public operations (SetLayers/Play/Stop/Seek) are serialized through a single dedicated
/// command thread (audit B1): MainWindow calls into this engine from several places --
/// a debounced live-refresh, transport button clicks, both via Task.Run -- and without
/// serialization, overlapping calls could interleave a Stop and a Play, dispose frame sources out
/// from under an in-flight render, or start two audio sinks at once. Routing every call through
/// one queue makes each operation atomic relative to the others.
/// </summary>
public class PreviewPlaybackEngine : IDisposable
{
    private readonly MixEngine _mixEngine = new();
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly int _sampleRate;
    private readonly int _fps;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private readonly IPreviewAudioSink _audioSink;

    private List<LayerModel> _layers = new();
    private List<ILayerFrameSource>? _frameSources;
    private Thread? _frameLoopThread;
    private volatile bool _stopRequested;

    // Single-threaded command queue (audit B1): every public operation below enqueues work here
    // instead of running inline, so SetLayers/Play/Stop/Seek from any caller thread never
    // interleave. Core methods call each other directly (never via Enqueue) to avoid a command
    // waiting on itself.
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _commandQueue = new();
    private readonly Thread _commandThread;

    public event Action<SKBitmap>? FrameReady;
    public event Action? PlaybackStopped;

    public bool IsPlaying { get; private set; }
    public double PositionMs { get; private set; }
    public double DurationMs { get; private set; }

    public PreviewPlaybackEngine(int canvasWidth = 640, int canvasHeight = 480, int fps = 30, int sampleRate = 44100, string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe", IPreviewAudioSink? audioSink = null)
    {
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _fps = fps;
        _sampleRate = sampleRate;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _audioSink = audioSink ?? new WasapiPreviewAudioSink();

        _commandThread = new Thread(RunCommandLoop) { IsBackground = true };
        _commandThread.Start();
    }

    private void RunCommandLoop()
    {
        foreach (var command in _commandQueue.GetConsumingEnumerable())
            command();
    }

    private void Enqueue(Action action)
    {
        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        _commandQueue.Add(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null)
            throw new AggregateException(error);
    }

    /// <summary>Rebinds the layer set this engine plays. Callers should call this whenever
    /// sources/trim/FX change, then Seek(PositionMs) to refresh the visible frame at the same
    /// spot (the caller decides whether that also means "keep playing").</summary>
    public void SetLayers(IReadOnlyList<LayerModel> layers) => Enqueue(() => SetLayersCore(layers));

    private void SetLayersCore(IReadOnlyList<LayerModel> layers)
    {
        _layers = layers.ToList();
        DurationMs = ComputeDurationMs();
        PositionMs = Math.Min(PositionMs, DurationMs);
    }

    private double ComputeDurationMs()
    {
        if (_layers.Count == 0) return 0;

        double maxMs = 0;
        foreach (var layer in _layers)
            maxMs = Math.Max(maxMs, LayerDurationMs(layer));
        return maxMs;
    }

    /// <summary>Layer duration from ffprobe's container duration (audit A5) -- correct for
    /// video-only and audio-only layers alike, and avoids a full ffmpeg audio decode just to
    /// measure length (audit B2). Trim/shift are applied to the probed duration the same way
    /// TrimHelper/AudioShiftHelper apply them to decoded sample arrays.</summary>
    internal double LayerDurationMs(LayerModel layer)
    {
        double rawMs = Ffmpeg.MediaProbe.GetDurationSeconds(layer.SourcePath, _ffprobePath) * 1000.0;
        double trimEndMs = Math.Min(layer.TrimEndMs ?? rawMs, rawMs);
        double trimmedMs = Math.Max(0, trimEndMs - layer.TrimStartMs);

        double shiftMs = layer.GetShiftMs();
        return shiftMs >= 0 ? trimmedMs + shiftMs : Math.Max(0, trimmedMs + shiftMs);
    }

    public void Play() => Enqueue(PlayCore);

    private void PlayCore()
    {
        if (IsPlaying || _layers.Count == 0) return;
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
        var positionTracker = StartAudio(startPositionMs);
        bool useAudioClock = _audioSink.DrivesRealtime;

        IsPlaying = true;
        _stopRequested = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        _frameLoopThread = new Thread(() => FrameLoop(clock, positionTracker, useAudioClock, startPositionMs, sources, lastFrames, consumedFrames)) { IsBackground = true };
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
    private void FrameLoop(System.Diagnostics.Stopwatch clock, PositionTrackingSampleProvider? positionTracker, bool useAudioClock, double startPositionMs, IReadOnlyList<ILayerFrameSource> sources, SKBitmap[] lastFrames, int[] consumedFrames)
    {
        while (!_stopRequested)
        {
            double elapsedMs = useAudioClock && positionTracker is not null
                ? positionTracker.PositionMs
                : clock.Elapsed.TotalMilliseconds;
            PositionMs = Math.Min(startPositionMs + elapsedMs, DurationMs);
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

        StopInternal(raiseStoppedEvent: true);
    }

    private void RenderComposite(IReadOnlyList<SKBitmap> frames)
    {
        var cellRects = Layout2x2Provider.GetCellRects(_canvasWidth, _canvasHeight, frames.Count);
        var composited = Compositor.Composite(_canvasWidth, _canvasHeight, frames, cellRects);
        FrameReady?.Invoke(composited);
    }

    private void RenderCurrentFrame(IReadOnlyList<ILayerFrameSource> sources)
    {
        var frames = sources.Select(s => s.GetNextFrame()).ToList();
        RenderComposite(frames);
    }

    private void StartFrameSources(double positionMs)
    {
        _frameSources = _layers.Select(layer => CreateFrameSource(layer, positionMs)).ToList();
    }

    private ILayerFrameSource CreateFrameSource(LayerModel layer, double positionMs)
    {
        int cellWidth = _canvasWidth / 2;
        int cellHeight = _canvasHeight / 2;

        if (layer.Kind == LayerKind.UploadedAudioOnly)
            return new StaticFrameSource(PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight));

        // Generalizes VideoFrameStreamSource's shift/trim formulas (see its own doc comment) to
        // an arbitrary playback start position P. For a negative shift, ExportEngine passes
        // shiftMs straight through so VideoFrameStreamSource's own -ss math applies the
        // |shiftMs| head-skip; the preview path instead folds the skip directly into media time
        // here (since it also needs the position-dependent hold term), so that same |shiftMs|
        // head-skip must be included explicitly -- omitting it left every recorded layer's
        // preview video lagging its own audio by the calibration offset (audit A3). For a
        // positive shift, the layer still owes max(0, shiftMs - P) of hold before real content
        // begins; passing that residual as the "shiftMs" argument (always >= 0 here) reuses
        // VideoFrameStreamSource's own hold-only branch without re-applying the skip a second
        // time.
        double shiftMs = layer.GetShiftMs();
        double headSkipMs = Math.Max(0, -shiftMs);
        double effectiveTrimStart = layer.TrimStartMs + headSkipMs + Math.Max(0, positionMs - Math.Max(0, shiftMs));
        double residualHoldMs = Math.Max(0, shiftMs - positionMs);

        return new VideoFrameStreamSource(layer.SourcePath, cellWidth, cellHeight, _fps, residualHoldMs, _ffmpegPath, effectiveTrimStart, layer.TrimEndMs);
    }

    private PositionTrackingSampleProvider StartAudio(double positionMs)
    {
        const int sampleRate = 44100;
        var mixInputs = _layers
            .Select(l => new MixLayerInput(l.LayerId, AudioShiftHelper.ApplyShift(
                TrimHelper.ApplyTrim(AudioDecodeCache.GetOrDecode(l.SourcePath, sampleRate, _ffmpegPath), l.TrimStartMs, l.TrimEndMs, sampleRate),
                l.GetShiftMs(), sampleRate), sampleRate, l.MixParameters))
            .ToList();

        var mix = _mixEngine.BuildMix(mixInputs, sampleRate);
        ISampleProvider seeked = positionMs > 0
            ? new OffsetSampleProvider(mix) { SkipOver = TimeSpan.FromMilliseconds(positionMs) }
            : mix;

        // Wraps the post-seek stream so samples actually pulled by the sink count from zero at
        // this playback's start position -- FrameLoop adds startPositionMs back on top (audit A2).
        var tracked = new PositionTrackingSampleProvider(seeked);
        _audioSink.Play(tracked);
        return tracked;
    }

    public void Stop() => Enqueue(StopCore);

    private void StopCore()
    {
        _stopRequested = true;
        _frameLoopThread?.Join(2000);
        StopInternal(raiseStoppedEvent: false);
    }

    private void StopInternal(bool raiseStoppedEvent)
    {
        _audioSink.Stop();

        if (_frameSources is not null)
        {
            foreach (var s in _frameSources) s.Dispose();
            _frameSources = null;
        }

        IsPlaying = false;
        if (raiseStoppedEvent) PlaybackStopped?.Invoke();
    }

    public void Restart() => Seek(0);

    /// <summary>Seeks to positionMs. If currently playing, restarts playback from the new
    /// position; if paused, renders a single static composited frame at that position so
    /// scrubbing while stopped still updates the preview. Atomic relative to concurrent
    /// Play/Stop/SetLayers calls (audit B1) since it runs entirely on the command thread.</summary>
    public void Seek(double positionMs) => Enqueue(() => SeekCore(positionMs));

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
            var sources = _layers.Select(layer => CreateFrameSource(layer, positionMs)).ToList();
            RenderCurrentFrame(sources);
            foreach (var s in sources) s.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _commandQueue.CompleteAdding();
        _commandThread.Join();
        _audioSink.Dispose();
    }
}
