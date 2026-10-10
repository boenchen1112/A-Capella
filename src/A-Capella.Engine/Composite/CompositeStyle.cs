namespace A_Capella.Engine.Composite;

public enum BackgroundImageMode
{
    Stretch,
    Tile,
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

public sealed record CompositeStyle(BackgroundSettings Background)
{
    public static CompositeStyle Default { get; } = new(new BackgroundSettings());
}
