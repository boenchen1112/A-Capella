namespace Acapella.App.Layout;

/// <summary>Pure pixel&lt;-&gt;millisecond math for a lane's clip bar (the dashed full-source bar
/// and the solid trimmed-span bar drawn on top of it) and for dragging its trim handles. Kept free
/// of any WPF types so it's unit-testable without an STA thread, and so MainWindow's drag-handle
/// code and the lane-rendering code share exactly one definition of "where does this trim point
/// land on screen" / "what trim value does this screen position mean".</summary>
public static class ClipTimelineMath
{
    /// <summary>The smallest gap a drag may leave between the in and out points, in milliseconds --
    /// without a floor, a handle dragged past its opposite number would produce a
    /// negative-or-zero-length (or inverted) trim span.</summary>
    public const double MinTrimGapMs = 50;

    /// <summary>Maps a [startMs, endMs) span onto a horizontal pixel range within a track of
    /// trackWidthPx that represents timelineDurationMs of project time, starting at pixel 0 = time
    /// 0. Returns (0, 0) if the scale is degenerate (no duration yet, or a not-yet-laid-out track).</summary>
    public static (double X, double Width) ToPixelRange(double startMs, double endMs, double timelineDurationMs, double trackWidthPx)
    {
        if (timelineDurationMs <= 0 || trackWidthPx <= 0) return (0, 0);
        double pxPerMs = trackWidthPx / timelineDurationMs;
        double x = Math.Max(0, startMs) * pxPerMs;
        double width = Math.Max(0, endMs - startMs) * pxPerMs;
        return (x, width);
    }

    /// <summary>Converts a horizontal drag distance (pixels) to milliseconds at the given scale.
    /// Returns 0 for a not-yet-laid-out track rather than dividing by zero.</summary>
    public static double PixelDeltaToMs(double deltaPx, double timelineDurationMs, double trackWidthPx) =>
        trackWidthPx > 0 ? deltaPx * (timelineDurationMs / trackWidthPx) : 0;

    /// <summary>Clamps a candidate new trim-in value: never negative, and never within
    /// MinTrimGapMs of the current trim-out.</summary>
    public static double ClampTrimStart(double candidateMs, double trimEndMs) =>
        Math.Clamp(candidateMs, 0, Math.Max(0, trimEndMs - MinTrimGapMs));

    /// <summary>Clamps a candidate new trim-out value: never past the source's own duration, and
    /// never within MinTrimGapMs of the current trim-in. If sourceDurationMs isn't known yet (&lt;= 0,
    /// e.g. MediaProbe hasn't returned), the upper bound is the candidate itself rather than
    /// collapsing every drag down to trimStartMs + MinTrimGapMs -- callers should disable the
    /// out-handle until a real duration is available, but this keeps the math itself non-destructive
    /// if it's ever called before that.</summary>
    public static double ClampTrimEnd(double candidateMs, double trimStartMs, double sourceDurationMs)
    {
        double lower = trimStartMs + MinTrimGapMs;
        double upper = sourceDurationMs > 0 ? Math.Max(lower, sourceDurationMs) : Math.Max(lower, candidateMs);
        return Math.Clamp(candidateMs, lower, upper);
    }
}
