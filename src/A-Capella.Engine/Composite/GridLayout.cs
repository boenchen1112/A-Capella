using SkiaSharp;

namespace A_Capella.Engine.Composite;

/// <summary>A cell rectangle in normalized canvas units (0..1 on both axes).</summary>
public readonly record struct NormalizedRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

/// <summary>
/// A named arrangement of cells on the canvas. Cells are stored normalized so the same layout works
/// at every aspect ratio; a layer's CellIndex selects the cell it occupies. Layouts are plain data
/// (the Data Model Rule: layout id + per-layer cell index, never hardcoded positions).
/// </summary>
public sealed class GridLayout
{
    public GridLayout(string id, IReadOnlyList<NormalizedRect> cells)
    {
        if (cells.Count == 0) throw new ArgumentException("A layout needs at least one cell.", nameof(cells));
        Id = id;
        Cells = cells;
    }

    public string Id { get; }
    public IReadOnlyList<NormalizedRect> Cells { get; }

    /// <summary>The rect for one cell. gutterFraction is the gap between neighboring cells as a
    /// fraction of the canvas's shorter side; each cell is inset by half of it, so the outer margin
    /// is half the inner gap and a gutter of 0 tiles the canvas exactly. Indices beyond the layout
    /// clamp to its last cell (callers keep layouts large enough, but this never indexes out of range).</summary>
    public SKRect GetCellRect(int cellIndex, int canvasWidth, int canvasHeight, double gutterFraction = 0)
    {
        var c = Cells[Math.Clamp(cellIndex, 0, Cells.Count - 1)];
        float inset = (float)(Math.Max(0, gutterFraction) * Math.Min(canvasWidth, canvasHeight) / 2);
        var rect = new SKRect(c.Left * canvasWidth, c.Top * canvasHeight, c.Right * canvasWidth, c.Bottom * canvasHeight);
        // Never let the inset swallow a cell.
        float maxInsetX = Math.Max(0, rect.Width / 2 - 1), maxInsetY = Math.Max(0, rect.Height / 2 - 1);
        rect.Inflate(-Math.Min(inset, maxInsetX), -Math.Min(inset, maxInsetY));
        return rect;
    }

    /// <summary>One rect per entry in cellIndices, each the actual cell that index names -- does not
    /// assume cells are filled contiguously from 0, so a lone layer at CellIndex 1 still lands in
    /// the second cell instead of being compacted into the first.</summary>
    public IReadOnlyList<SKRect> GetCellRects(int canvasWidth, int canvasHeight, double gutterFraction, IReadOnlyList<int> cellIndices) =>
        cellIndices.Select(i => GetCellRect(i, canvasWidth, canvasHeight, gutterFraction)).ToList();

    /// <summary>The first layerCount cells; only for callers whose layers occupy cells 0..n-1.</summary>
    public IReadOnlyList<SKRect> GetCellRects(int canvasWidth, int canvasHeight, double gutterFraction, int layerCount) =>
        GetCellRects(canvasWidth, canvasHeight, gutterFraction, Enumerable.Range(0, Math.Max(0, layerCount)).ToList());

    /// <summary>Pixel size a frame source should produce for a cell (never below 2x2).</summary>
    public (int Width, int Height) GetCellPixelSize(int cellIndex, int canvasWidth, int canvasHeight, double gutterFraction = 0)
    {
        var r = GetCellRect(cellIndex, canvasWidth, canvasHeight, gutterFraction);
        return (Math.Max(2, (int)Math.Round(r.Width)), Math.Max(2, (int)Math.Round(r.Height)));
    }
}

/// <summary>The built-in layouts (1 to 9 cells), generated from row/column recipes rather than
/// hand-listed rectangles. Cell order is reading order: left to right, top to bottom (column
/// recipes run top to bottom, then left to right).</summary>
public static class GridLayoutCatalog
{
    /// <summary>The id every project written before layouts existed carries; also the default.</summary>
    public const string LegacyId = "2x2";

    private static readonly List<GridLayout> All = Build();
    private static readonly Dictionary<string, GridLayout> ById = All.ToDictionary(l => l.Id);

    public static IReadOnlyList<GridLayout> Layouts => All;
    public static GridLayout Grid2x2 => ById[LegacyId];

    /// <summary>The layout for id, or the 2x2 layout for null/unknown ids (old or hand-edited files).</summary>
    public static GridLayout Get(string? id) => id is not null && ById.TryGetValue(id, out var l) ? l : Grid2x2;

    public static bool Exists(string? id) => id is not null && ById.ContainsKey(id);

    public static IEnumerable<GridLayout> WithAtLeastCells(int cellCount) => All.Where(l => l.Cells.Count >= cellCount);

    /// <summary>The layout a project should switch to when it needs requiredCells and currentId is
    /// too small: the first (preferred) layout with exactly that many cells, else the smallest that fits.</summary>
    public static string EnsureFits(string? currentId, int requiredCells)
    {
        if (Get(currentId).Cells.Count >= requiredCells) return Get(currentId).Id;
        return (All.FirstOrDefault(l => l.Cells.Count == requiredCells)
                ?? All.Where(l => l.Cells.Count >= requiredCells).OrderBy(l => l.Cells.Count).First()).Id;
    }

    private static List<GridLayout> Build() => new()
    {
        Rows("1", 1),

        Rows("2-rows", 1, 1),
        Cols("2-cols", 1, 1),

        Rows("3-rows", 1, 1, 1),
        Cols("3-cols", 1, 1, 1),
        Rows("1-over-2", 1, 2),
        Rows("2-over-1", 2, 1),
        Cols("1-beside-2", 1, 2),
        Cols("2-beside-1", 2, 1),

        Rows(LegacyId, 2, 2),
        Rows("4-rows", 1, 1, 1, 1),
        Cols("4-cols", 1, 1, 1, 1),
        Rows("1-over-3", 1, 3),
        Rows("3-over-1", 3, 1),
        Cols("1-beside-3", 1, 3),
        Cols("3-beside-1", 3, 1),

        Rows("2-over-3", 2, 3),
        Rows("3-over-2", 3, 2),
        Cols("2-beside-3", 2, 3),
        Cols("3-beside-2", 3, 2),
        Rows("1-over-4", 1, 4),
        Rows("4-over-1", 4, 1),

        Rows("2x3", 3, 3),
        Rows("3x2", 2, 2, 2),
        Rows("1-2-3", 1, 2, 3),
        Rows("3-2-1", 3, 2, 1),

        Rows("3-over-4", 3, 4),
        Rows("4-over-3", 4, 3),
        Rows("3-2-2", 3, 2, 2),
        Rows("2-2-3", 2, 2, 3),

        Rows("2x4", 4, 4),
        Rows("4x2", 2, 2, 2, 2),
        Rows("3-3-2", 3, 3, 2),
        Rows("2-3-3", 2, 3, 3),

        Rows("3x3", 3, 3, 3),
        Rows("2-3-4", 2, 3, 4),
        Rows("4-3-2", 4, 3, 2),
        Rows("1-4-4", 1, 4, 4),
    };

    /// <summary>Equal-height rows; row i holds perRow[i] equal-width cells.</summary>
    private static GridLayout Rows(string id, params int[] perRow)
    {
        var cells = new List<NormalizedRect>();
        for (int r = 0; r < perRow.Length; r++)
            for (int c = 0; c < perRow[r]; c++)
                cells.Add(new NormalizedRect((float)c / perRow[r], (float)r / perRow.Length, (float)(c + 1) / perRow[r], (float)(r + 1) / perRow.Length));
        return new GridLayout(id, cells);
    }

    /// <summary>Equal-width columns; column i holds perCol[i] equal-height cells, filled top to bottom.</summary>
    private static GridLayout Cols(string id, params int[] perCol)
    {
        var cells = new List<NormalizedRect>();
        for (int c = 0; c < perCol.Length; c++)
            for (int r = 0; r < perCol[c]; r++)
                cells.Add(new NormalizedRect((float)c / perCol.Length, (float)r / perCol[c], (float)(c + 1) / perCol.Length, (float)(r + 1) / perCol[c]));
        return new GridLayout(id, cells);
    }
}
