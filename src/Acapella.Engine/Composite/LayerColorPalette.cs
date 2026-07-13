using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>Stable per-cell accent colors for grid identification (v5 P2 task 3): index-based, not
/// tied to a specific layer instance, so a layer keeps its color as long as it stays in the same
/// grid cell (CellIndex) even if other layers are added/removed around it.</summary>
public static class LayerColorPalette
{
    private static readonly SKColor[] Colors =
    {
        new(0xE0, 0x57, 0x57), // red
        new(0x57, 0xA0, 0xE0), // blue
        new(0x57, 0xC0, 0x7A), // green
        new(0xE0, 0xB0, 0x40), // amber
    };

    public static SKColor GetColor(int cellIndex) => Colors[((cellIndex % Colors.Length) + Colors.Length) % Colors.Length];
}
