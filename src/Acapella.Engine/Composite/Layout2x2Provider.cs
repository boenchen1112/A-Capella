using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>
/// The one piece of the compositing system that knows about the fixed 2x2 grid (locked product
/// decision). Cell order matches recording order: top-left, top-right, bottom-left, bottom-right.
/// </summary>
public static class Layout2x2Provider
{
    /// <summary>The first layerCount cells, top-left first. Only correct when the caller's frames
    /// are for layers occupying cells 0..layerCount-1 contiguously -- a layer sitting at a higher
    /// CellIndex with lower cells empty (e.g. the only layer is at CellIndex 1) must use the
    /// GetCellRects(IReadOnlyList&lt;int&gt;) overload instead, or it silently compacts into the
    /// wrong cell.</summary>
    public static IReadOnlyList<SKRect> GetCellRects(int canvasWidth, int canvasHeight, int layerCount) =>
        GetCellRects(canvasWidth, canvasHeight, Enumerable.Range(0, Math.Max(0, layerCount)).ToList());

    /// <summary>One rect per entry in cellIndices, each the actual grid cell that index names --
    /// unlike the layerCount overload, this doesn't assume cells are filled contiguously from 0, so
    /// a lone layer at CellIndex 1 (with cell 0 empty/undrawn) still composites into the top-right
    /// cell instead of being compacted into the top-left one. Indices beyond the fixed 2x2 grid are
    /// clamped to the last cell (shouldn't happen given the 4-layer cap, but keeps this from ever
    /// indexing out of range).</summary>
    public static IReadOnlyList<SKRect> GetCellRects(int canvasWidth, int canvasHeight, IReadOnlyList<int> cellIndices)
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

        return cellIndices.Select(i => allCells[Math.Clamp(i, 0, allCells.Length - 1)]).ToList();
    }
}
