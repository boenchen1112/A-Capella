using Acapella.Engine.Composite;

namespace Acapella.Engine.Project;

public enum AspectRatioOption
{
    /// <summary>16:9, the original (and default) canvas.</summary>
    Wide16x9,
    Square1x1,
    Standard4x3,
    Tall9x16,
}

/// <summary>Project-level look of the composited video: layout, canvas shape, how videos fill their
/// cells and the gap between cells. Immutable; change it with <c>with</c> and assign it back to
/// ProjectSession.Video.</summary>
public sealed record VideoSettings
{
    public const double MaxGutterFraction = 0.05;

    public string LayoutId { get; init; } = GridLayoutCatalog.LegacyId;
    public AspectRatioOption AspectRatio { get; init; } = AspectRatioOption.Wide16x9;
    public CellFit Fit { get; init; } = CellFit.Fill;

    /// <summary>Gap between cells as a fraction of the canvas's shorter side (0 to MaxGutterFraction).</summary>
    public double GutterFraction { get; init; } = 0.01;

    /// <summary>Settings of a project saved before video settings existed: no gap and letterboxed
    /// videos, so it renders exactly as it did when it was saved.</summary>
    public static VideoSettings Legacy(string layoutId) => new() { LayoutId = layoutId, Fit = CellFit.Fit, GutterFraction = 0 };

    public GridLayout Layout => GridLayoutCatalog.Get(LayoutId);

    /// <summary>Exported video size in pixels (even dimensions, as H.264 requires).</summary>
    public (int Width, int Height) ExportSize => SizeFor(AspectRatio);

    /// <summary>Preview canvas size: half the export size, so cell math stays exact integers.</summary>
    public (int Width, int Height) PreviewSize
    {
        get { var (w, h) = ExportSize; return (w / 2, h / 2); }
    }

    public static (int Width, int Height) SizeFor(AspectRatioOption ratio) => ratio switch
    {
        AspectRatioOption.Square1x1 => (1080, 1080),
        AspectRatioOption.Standard4x3 => (1440, 1080),
        AspectRatioOption.Tall9x16 => (720, 1280),
        _ => (1280, 720),
    };

    public static string Describe(AspectRatioOption ratio) => ratio switch
    {
        AspectRatioOption.Square1x1 => "1:1 (square)",
        AspectRatioOption.Standard4x3 => "4:3",
        AspectRatioOption.Tall9x16 => "9:16 (vertical)",
        _ => "16:9 (widescreen)",
    };
}
