using A_Capella.Engine.Mix;
using A_Capella.Engine.Project;

namespace A_Capella.Engine.Sync;

/// <summary>Lines one layer up with another (normally layer 1, the guide everyone sings to).</summary>
public static class LayerAligner
{
    private const int SampleRate = 44100;

    /// <summary>Measures how late the target runs against the reference. Both are compared as they
    /// are trimmed, before any sync shift, so the result does not depend on an earlier alignment.
    /// Slow (decoding plus, with a note source, plugin analysis): run it off the UI thread.</summary>
    public static AlignmentResult Measure(LayerModel reference, LayerModel target, INoteSource? notes, string ffmpegPath = "ffmpeg") =>
        TrackAligner.Align(
            Trimmed(reference, ffmpegPath), Trimmed(target, ffmpegPath), SampleRate,
            notes, ContentKey(reference), ContentKey(target));

    /// <summary>Makes the target start together with the reference. Replaces the target's manual nudge,
    /// which was relative to the old position.</summary>
    public static void Apply(LayerModel reference, LayerModel target, AlignmentResult result)
    {
        target.ManualOffsetMs = 0;
        target.CalibratedOffsetMs = result.OffsetMs - reference.GetShiftMs();
    }

    /// <summary>Identifies what was analysed: the source file and its trim, but not the sync shift.</summary>
    public static string ContentKey(LayerModel layer)
    {
        long mtimeTicks = File.Exists(layer.SourcePath) ? File.GetLastWriteTimeUtc(layer.SourcePath).Ticks : 0;
        return $"{layer.SourcePath}|{mtimeTicks}|{layer.TrimStartMs}|{layer.TrimEndMs}";
    }

    private static float[] Trimmed(LayerModel layer, string ffmpegPath) =>
        TrimHelper.ApplyTrim(AudioDecodeCache.GetOrDecode(layer.SourcePath, SampleRate, ffmpegPath), layer.TrimStartMs, layer.TrimEndMs, SampleRate);
}
