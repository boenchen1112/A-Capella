using SkiaSharp;

namespace A_Capella.Engine.Composite;

/// <summary>Stable per-cell accent colors for grid identification (v5 P2 task 3): index-based, not
/// tied to a specific layer instance, so a layer keeps its color as long as it stays in the same
/// grid cell (CellIndex) even if other layers are added/removed around it.</summary>
public static class LayerColorPalette
{
    private static readonly SKColor[] Colors =
    {
        new(0xE2, 0x6D, 0x6D), // red
        new(0x5E, 0xA2, 0xE6), // blue
        new(0x5C, 0xC4, 0x89), // green
        new(0xE2, 0xB5, 0x4A), // amber
        new(0xA0, 0x70, 0xE0), // violet
        new(0x40, 0xC8, 0xC8), // teal
        new(0xE0, 0x80, 0x40), // orange
        new(0xE0, 0x70, 0xB0), // pink
        new(0x90, 0xB0, 0x40), // olive
    };

    public static SKColor GetColor(int cellIndex) => Colors[((cellIndex % Colors.Length) + Colors.Length) % Colors.Length];
}
