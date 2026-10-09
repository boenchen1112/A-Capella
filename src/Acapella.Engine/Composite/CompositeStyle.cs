namespace Acapella.Engine.Composite;

public enum BackgroundImageMode
{
    Stretch,
    Tile,
}

public enum WatermarkCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
}

/// <summary>What shows behind the cells and in the gaps/empty cells: a color, optionally covered by an
/// image. Colors are "#RRGGBB" strings so the settings stay plain data (they go straight into the
/// project file); unparseable colors fall back to the defaults in StyleRenderer.</summary>
public sealed record BackgroundSettings
{
    public string Color { get; init; } = "#000000";
    public string? ImagePath { get; init; }
    public BackgroundImageMode ImageMode { get; init; } = BackgroundImageMode.Stretch;
}

/// <summary>A text overlay drawn over the finished composite (preview and export alike).
/// SizeFraction is the font size as a fraction of the canvas height; StrokeFraction the outline
/// width as a fraction of the font size; Opacity applies to the whole mark.</summary>
public sealed record WatermarkSettings
{
    public const double MinSizeFraction = 0.02;
    public const double MaxSizeFraction = 0.2;

    public bool Enabled { get; init; }
    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "Arial";
    public double SizeFraction { get; init; } = 0.06;
    public string FillColor { get; init; } = "#FFFFFF";
    public string StrokeColor { get; init; } = "#000000";
    public double StrokeFraction { get; init; } = 0.08;
    public bool Shadow { get; init; } = true;
    public double Opacity { get; init; } = 0.85;
    public WatermarkCorner Corner { get; init; } = WatermarkCorner.BottomRight;

    /// <summary>True when drawing it would put anything on screen.</summary>
    public bool IsVisible => Enabled && !string.IsNullOrWhiteSpace(Text) && Opacity > 0;
}

public sealed record CompositeStyle(BackgroundSettings Background, WatermarkSettings Watermark)
{
    public static CompositeStyle Default { get; } = new(new BackgroundSettings(), new WatermarkSettings());
}
