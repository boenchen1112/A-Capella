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
}

/// <summary>Default sink: the system's default render device via WasapiOut.</summary>
public class WasapiPreviewAudioSink : IPreviewAudioSink
{
    private WasapiOut? _output;

    public void Play(ISampleProvider mix)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
/// </summary>
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
/// </summary>
public class PreviewPlaybackEngine : IDisposable
{
    private readonly MixEngine _mixEngine = new();
    private readonly string _ffmpegPath;
    private readonly int _sampleRate;
    private readonly int _fps;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    private readonly IPreviewAudioSink _audioSink;

    private List<LayerModel> _layers = new();
    private List<ILayerFrameSource>? _frameSources;
    private Thread? _frameLoopThread;
    private volatile bool _stopRequested;

    public event Action<SKBitmap>? FrameReady;
    public event Action? PlaybackStopped;

    public bool IsPlaying { get; private set; }
    public double PositionMs { get; private set; }
    public double DurationMs { get; private set; }

    public PreviewPlaybackEngine(int canvasWidth = 640, int canvasHeight = 480, int fps = 30, int sampleRate = 44100, string ffmpegPath = "ffmpeg", IPreviewAudioSink? audioSink = null)
    {
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _fps = fps;
        _sampleRate = sampleRate;
        _ffmpegPath = ffmpegPath;
        _audioSink = audioSink ?? new WasapiPreviewAudioSink();
    }

    /// <summary>Rebinds the layer set this engine plays. Callers should call this whenever
    /// sources/trim/FX change, then Seek(PositionMs) to refresh the visible frame at the same
    /// spot (the caller decides whether that also means "keep playing").</summary>
    public void SetLayers(IReadOnlyList<LayerModel> layers)
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
        {
            float[] decoded = AudioDecoder.DecodeToMonoFloat(layer.SourcePath, _sampleRate, _ffmpegPath);
            float[] trimmed = TrimHelper.ApplyTrim(decoded, layer.TrimStartMs, layer.TrimEndMs, _sampleRate);
            float[] shifted = AudioShiftHelper.ApplyShift(trimmed, layer.GetShiftMs(), _sampleRate);
            maxMs = Math.Max(maxMs, shifted.Length * 1000.0 / _sampleRate);
        }
        return maxMs;
    }

    public void Play()
    {
        if (IsPlaying || _layers.Count == 0) return;
        StartFrameSources(PositionMs);
        StartAudio(PositionMs);

        IsPlaying = true;
        _stopRequested = false;
        double startPositionMs = PositionMs;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        _frameLoopThread = new Thread(() => FrameLoop(clock, startPositionMs)) { IsBackground = true };
        _frameLoopThread.Start();
    }

    private void FrameLoop(System.Diagnostics.Stopwatch clock, double startPositionMs)
    {
        var sources = _frameSources!;
        double frameIntervalMs = 1000.0 / _fps;

        while (!_stopRequested)
        {
            double elapsed = clock.Elapsed.TotalMilliseconds;
            PositionMs = startPositionMs + elapsed;

            if (PositionMs >= DurationMs)
            {
                PositionMs = DurationMs;
                RenderCurrentFrame(sources);
                break;
            }

            RenderCurrentFrame(sources);

            double nextFrameAt = elapsed + frameIntervalMs;
            int sleepMs = (int)(nextFrameAt - clock.Elapsed.TotalMilliseconds);
            if (sleepMs > 0) Thread.Sleep(sleepMs);
        }

        StopInternal(raiseStoppedEvent: true);
    }

    private void RenderCurrentFrame(IReadOnlyList<ILayerFrameSource> sources)
    {
        var frames = sources.Select(s => s.GetNextFrame()).ToList();
        var cellRects = Layout2x2Provider.GetCellRects(_canvasWidth, _canvasHeight, frames.Count);
        var composited = Compositor.Composite(_canvasWidth, _canvasHeight, frames, cellRects);
        FrameReady?.Invoke(composited);
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
        // an arbitrary playback start position P: the layer's local media time at project time P
        // is trimStart + max(0, P - shiftMs) once past its shift-delayed start, and it still owes
        // max(0, shiftMs - P) of hold before real content begins. Passing that residual as the
        // "shiftMs" argument (always >= 0 here) reuses VideoFrameStreamSource's own hold-only
        // branch without re-applying the skip a second time.
        double shiftMs = layer.GetShiftMs();
        double effectiveTrimStart = layer.TrimStartMs + Math.Max(0, positionMs - Math.Max(0, shiftMs));
        double residualHoldMs = Math.Max(0, shiftMs - positionMs);

        return new VideoFrameStreamSource(layer.SourcePath, cellWidth, cellHeight, _fps, residualHoldMs, _ffmpegPath, effectiveTrimStart, layer.TrimEndMs);
    }

    private void StartAudio(double positionMs)
    {
        const int sampleRate = 44100;
        var mixInputs = _layers
            .Select(l => new MixLayerInput(l.LayerId, AudioShiftHelper.ApplyShift(
                TrimHelper.ApplyTrim(AudioDecoder.DecodeToMonoFloat(l.SourcePath, sampleRate, _ffmpegPath), l.TrimStartMs, l.TrimEndMs, sampleRate),
                l.GetShiftMs(), sampleRate), sampleRate, l.MixParameters))
            .ToList();

        var mix = _mixEngine.BuildMix(mixInputs, sampleRate);
        ISampleProvider seeked = positionMs > 0
            ? new OffsetSampleProvider(mix) { SkipOver = TimeSpan.FromMilliseconds(positionMs) }
            : mix;

        _audioSink.Play(seeked);
    }

    public void Stop()
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
    /// scrubbing while stopped still updates the preview.</summary>
    public void Seek(double positionMs)
    {
        positionMs = Math.Clamp(positionMs, 0, DurationMs);
        bool wasPlaying = IsPlaying;
        if (IsPlaying) Stop();

        PositionMs = positionMs;

        if (wasPlaying)
        {
            Play();
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
        _audioSink.Dispose();
    }
}
