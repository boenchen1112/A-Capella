using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>
/// The one piece of the compositing system that knows about the fixed 2x2 grid (locked product
/// decision). Cell order matches recording order: top-left, top-right, bottom-left, bottom-right.
/// </summary>
public static class Layout2x2Provider
{
    public static IReadOnlyList<SKRect> GetCellRects(int canvasWidth, int canvasHeight, int layerCount)
    {
        float halfWidth = canvasWidth / 2f;
        float halfHeight = canvasHeight / 2f;

        var allCells = new[]
        {
            new SKRect(0, 0, halfWidth, halfHeight),                       // top-left
            new SKRect(halfWidth, 0, canvasWidth, halfHeight),              // top-right
            new SKRect(0, halfHeight, halfWidth, canvasHeight),             // bottom-left
            new SKRect(halfWidth, halfHeight, canvasWidth, canvasHeight),   // bottom-right
        };

        return allCells.Take(Math.Min(layerCount, allCells.Length)).ToList();
    }
}
