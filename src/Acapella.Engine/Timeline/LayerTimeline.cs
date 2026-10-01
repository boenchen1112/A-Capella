using Acapella.Engine.Composite;
using Acapella.Engine.Export;
using Acapella.Engine.Ffmpeg;
using Acapella.Engine.Mix;
using Acapella.Engine.Project;

namespace Acapella.Engine.Timeline;

/// <summary>
/// Where each Layer sits on the project timeline: which part of its source plays (trim), when it
/// plays relative to the other layers (sync shift), and how long its own reverb tail rings past the
/// end. Preview, export, and the recording guide mix all ask this one module, so a sync/duration
/// fix lands everywhere at once -- audits A3/A4/A5/B8/Q2-3 each previously had to be fixed twice.
/// </summary>
public sealed class LayerTimeline
{
    private readonly MixEngine _mixEngine;
    private readonly string _ffmpegPath;
    private readonly Func<string, double> _probeDurationSeconds;

    public LayerTimeline(MixEngine mixEngine, string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
        : this(mixEngine, ffmpegPath, path => MediaProbe.GetDurationSeconds(path, ffprobePath))
    {
    }

    internal LayerTimeline(MixEngine mixEngine, string ffmpegPath, Func<string, double> probeDurationSeconds)
    {
        _mixEngine = mixEngine;
        _ffmpegPath = ffmpegPath;
        _probeDurationSeconds = probeDurationSeconds;
    }

    /// <summary>Grid order (audit B8): by CellIndex, not insertion order, so a layer attached to
    /// sidebar row 3 before row 2 still lands in grid cell 3. Frame sources and cell rects are
    /// zipped by this order.</summary>
    public static List<LayerModel> InCellOrder(IEnumerable<LayerModel> layers) =>
        layers.OrderBy(l => l.CellIndex).ToList();

    /// <summary>Project length: the longest layer, 0 with no layers.</summary>
    public double DurationMs(IEnumerable<LayerModel> layers, int sampleRate) =>
        layers.Select(l => DurationMs(l, sampleRate)).DefaultIfEmpty(0).Max();

    /// <summary>One layer's length on the timeline, from ffprobe's container duration (audit A5:
    /// correct for video-only and audio-only layers, no full decode needed), with trim and shift
    /// applied, extended by the layer's own clamped reverb tail (Q2 task 3).</summary>
    public double DurationMs(LayerModel layer, int sampleRate)
    {
        double rawMs = _probeDurationSeconds(layer.SourcePath) * 1000.0;
        double trimEndMs = Math.Min(layer.TrimEndMs ?? rawMs, rawMs);
        double trimmedMs = Math.Max(0, trimEndMs - layer.TrimStartMs);

        double shiftMs = layer.GetShiftMs();
        double totalMs = shiftMs >= 0 ? trimmedMs + shiftMs : Math.Max(0, trimmedMs + shiftMs);

        double tailMs = _mixEngine.GetTailSeconds(layer.LayerId, layer.MixParameters, sampleRate) * 1000.0;
        return totalMs + tailMs;
    }

    /// <summary>The layer's trimmed, shifted audio, ready for MixEngine, keyed so automatic pitch
    /// correction is cached across rebuilds (audit B3).</summary>
    public MixLayerInput AudioInput(LayerModel layer, int sampleRate)
    {
        var raw = AudioDecodeCache.GetOrDecode(layer.SourcePath, sampleRate, _ffmpegPath);
        var trimmed = TrimHelper.ApplyTrim(raw, layer.TrimStartMs, layer.TrimEndMs, sampleRate);
        var shifted = AudioShiftHelper.ApplyShift(trimmed, layer.GetShiftMs(), sampleRate);
        return new MixLayerInput(layer.LayerId, shifted, sampleRate, layer.MixParameters, layer.SourceCacheKey());
    }

    /// <summary>A frame source showing this layer's video from project position positionMs onward,
    /// in sync with AudioInput's samples at the same position. Audio-only layers get a static
    /// placeholder.</summary>
    public ILayerFrameSource FrameSource(LayerModel layer, int cellWidth, int cellHeight, int fps, double positionMs = 0)
    {
        if (layer.Kind == LayerKind.UploadedAudioOnly)
            return new StaticFrameSource(PlaceholderRenderer.CreateAudioOnlyPlaceholder(cellWidth, cellHeight));

        var window = VideoWindowAt(layer, positionMs);
        return new VideoFrameStreamSource(layer.SourcePath, cellWidth, cellHeight, fps, window.HoldMs, _ffmpegPath, window.SourceStartMs, layer.TrimEndMs);
    }

    /// <summary>SourceStartMs: where in the source file decoding begins. HoldMs: how long the
    /// placeholder frame holds before that content appears.</summary>
    internal readonly record struct VideoWindow(double SourceStartMs, double HoldMs);

    /// <summary>A negative shift skips |shift| into the source (audit A3 -- dropping that term left
    /// recorded layers' video lagging their audio); a positive shift holds for whatever of the shift
    /// is still ahead of positionMs. HoldMs is always >= 0, so VideoFrameStreamSource's own
    /// negative-shift branch never re-applies the skip, and its -t span ends at TrimEndMs measured
    /// from SourceStartMs (audit A4).</summary>
    internal static VideoWindow VideoWindowAt(LayerModel layer, double positionMs)
    {
        double shiftMs = layer.GetShiftMs();
        double headSkipMs = Math.Max(0, -shiftMs);
        double sourceStartMs = layer.TrimStartMs + headSkipMs + Math.Max(0, positionMs - Math.Max(0, shiftMs));
        double holdMs = Math.Max(0, shiftMs - positionMs);
        return new VideoWindow(sourceStartMs, holdMs);
    }
}
