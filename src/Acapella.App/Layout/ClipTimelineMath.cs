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

    /// <summary>The ruler's tick spacing: the smallest "nice" interval (in seconds) whose pixel
    /// spacing at the track's current scale is at least minPxBetweenTicks, so ticks never crowd
    /// together on a short or heavily zoomed-out timeline. Empty for a degenerate scale.</summary>
    private static readonly double[] NiceTickIntervalsSeconds = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };

    public static IReadOnlyList<double> ComputeTickTimesMs(double durationMs, double trackWidthPx, double minPxBetweenTicks = 60)
    {
        if (durationMs <= 0 || trackWidthPx <= 0) return Array.Empty<double>();

        double pxPerMs = trackWidthPx / durationMs;
        double intervalSeconds = NiceTickIntervalsSeconds[^1];
        foreach (double candidate in NiceTickIntervalsSeconds)
        {
            if (candidate * 1000 * pxPerMs >= minPxBetweenTicks)
            {
                intervalSeconds = candidate;
                break;
            }
        }

        double intervalMs = intervalSeconds * 1000;
        var ticks = new List<double>();
        for (double t = 0; t <= durationMs + 0.001; t += intervalMs)
            ticks.Add(t);
        return ticks;
    }

    /// <summary>Ruler tick label: "m:ss" (the mock's "0:00", "0:30", "1:00").</summary>
    public static string FormatTickLabel(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}

/// <summary>Tracks one in-progress trim-handle drag on a lane's clip bar: converts the total mouse
/// movement since the drag started into a clamped trim value, and reports whether the value
/// actually changed so the caller commits at most one undo step per completed drag (and none for a
/// click with no movement). Deliberately stateless beyond its constructor snapshot -- the caller
/// (MainWindow) owns the live LayerRowViewModel and applies ValueAt's result to it on every
/// DragDelta for live visual feedback, matching the CommitSlider pattern (apply live, commit once on
/// release).</summary>
public sealed class ClipTrimDrag
{
    public enum Handle { In, Out }

    private readonly Handle _handle;
    private readonly double _startValueMs;
    private readonly double _otherBoundMs;
    private readonly double _sourceDurationMs;
    private readonly double _timelineDurationMs;
    private readonly double _trackWidthPx;

    /// <summary>startValueMs is the handle's own value when the drag began (TrimStartMs for an In
    /// drag, EffectiveTrimEndMs for an Out drag). otherBoundMs is the OTHER end, fixed for the
    /// duration of the drag (EffectiveTrimEndMs for an In drag, TrimStartMs for an Out drag).</summary>
    public ClipTrimDrag(Handle handle, double startValueMs, double otherBoundMs, double sourceDurationMs, double timelineDurationMs, double trackWidthPx)
    {
        _handle = handle;
        _startValueMs = startValueMs;
        _otherBoundMs = otherBoundMs;
        _sourceDurationMs = sourceDurationMs;
        _timelineDurationMs = timelineDurationMs;
        _trackWidthPx = trackWidthPx;
    }

    /// <summary>cumulativeDeltaPx is the total horizontal mouse movement since the drag started
    /// (not a per-event delta -- summing per-event deltas can double-count against a handle whose
    /// own on-screen position is also bound to the value being dragged). Rounded to whole
    /// milliseconds so the channel panel's In/Out text fields show clean numbers.</summary>
    public double ValueAt(double cumulativeDeltaPx)
    {
        double deltaMs = ClipTimelineMath.PixelDeltaToMs(cumulativeDeltaPx, _timelineDurationMs, _trackWidthPx);
        double candidateMs = _startValueMs + deltaMs;
        return Math.Round(_handle == Handle.In
            ? ClipTimelineMath.ClampTrimStart(candidateMs, _otherBoundMs)
            : ClipTimelineMath.ClampTrimEnd(candidateMs, _otherBoundMs, _sourceDurationMs));
    }

    public bool HasChangedFrom(double currentValueMs) => currentValueMs != _startValueMs;
}
