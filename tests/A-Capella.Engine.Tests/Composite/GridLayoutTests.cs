using SkiaSharp;
using A_Capella.Engine.Composite;

namespace A_Capella.Engine.Tests.Composite;

public class GridLayoutTests
{
    public static IEnumerable<object[]> AllLayouts => GridLayoutCatalog.Layouts.Select(l => new object[] { l.Id });

    [Fact]
    public void Catalog_CoversOneToNineCells_WithUniqueIds()
    {
        var layouts = GridLayoutCatalog.Layouts;
        Assert.Equal(layouts.Count, layouts.Select(l => l.Id).Distinct().Count());
        foreach (int cells in Enumerable.Range(1, 9))
            Assert.Contains(layouts, l => l.Cells.Count == cells);
        Assert.DoesNotContain(layouts, l => l.Cells.Count is < 1 or > 9);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void EveryLayout_TilesTheCanvasExactlyWithNoOverlap(string id)
    {
        var layout = GridLayoutCatalog.Get(id);
        float area = 0;
        foreach (var c in layout.Cells)
        {
            Assert.InRange(c.Left, 0f, 1f); Assert.InRange(c.Right, 0f, 1f);
            Assert.InRange(c.Top, 0f, 1f); Assert.InRange(c.Bottom, 0f, 1f);
            Assert.True(c.Width > 0 && c.Height > 0);
            area += c.Width * c.Height;
        }
        Assert.Equal(1f, area, 3);

        for (int i = 0; i < layout.Cells.Count; i++)
            for (int j = i + 1; j < layout.Cells.Count; j++)
            {
                var a = layout.Cells[i]; var b = layout.Cells[j];
                bool overlap = a.Left < b.Right - 1e-4f && b.Left < a.Right - 1e-4f && a.Top < b.Bottom - 1e-4f && b.Top < a.Bottom - 1e-4f;
                Assert.False(overlap, $"{id}: cells {i} and {j} overlap");
            }
    }

    [Fact]
    public void Grid2x2_WithNoGutter_MatchesTheLegacyQuadrants()
    {
        var rects = GridLayoutCatalog.Grid2x2.GetCellRects(640, 360, 0, 4);
        Assert.Equal(new SkiaSharp.SKRect(0, 0, 320, 180), rects[0]);
        Assert.Equal(new SkiaSharp.SKRect(320, 0, 640, 180), rects[1]);
        Assert.Equal(new SkiaSharp.SKRect(0, 180, 320, 360), rects[2]);
        Assert.Equal(new SkiaSharp.SKRect(320, 180, 640, 360), rects[3]);
    }

    [Fact]
    public void Gutter_InsetsEachCellByHalfTheGap_SoOuterMarginIsHalfTheInnerGap()
    {
        // 1000x500 canvas, 4% of the shorter side = 20px gap.
        var rects = GridLayoutCatalog.Get("2-cols").GetCellRects(1000, 500, 0.04, 2);
        Assert.Equal(10f, rects[0].Left, 3);
        Assert.Equal(10f, rects[0].Top, 3);
        Assert.Equal(10f, 500 - rects[0].Bottom, 3);
        float innerGap = rects[1].Left - rects[0].Right;
        Assert.Equal(20f, innerGap, 3);
    }

    [Fact]
    public void GetCellRects_KeysByCellIndex_NotByPosition()
    {
        var rects = GridLayoutCatalog.Grid2x2.GetCellRects(640, 360, 0, new[] { 1 });
        Assert.Equal(new SkiaSharp.SKRect(320, 0, 640, 180), rects[0]);
    }

    [Fact]
    public void OutOfRangeCellIndex_ClampsToLastCell()
    {
        var layout = GridLayoutCatalog.Grid2x2;
        Assert.Equal(layout.GetCellRect(3, 640, 360), layout.GetCellRect(99, 640, 360));
    }

    [Fact]
    public void Get_UnknownOrNullId_FallsBackToTheLegacy2x2Layout()
    {
        Assert.Equal("2x2", GridLayoutCatalog.Get(null).Id);
        Assert.Equal("2x2", GridLayoutCatalog.Get("no-such-layout").Id);
        Assert.False(GridLayoutCatalog.Exists("no-such-layout"));
    }

    [Fact]
    public void EnsureFits_KeepsALayoutThatFits_AndGrowsOneThatDoesNot()
    {
        Assert.Equal("2x2", GridLayoutCatalog.EnsureFits("2x2", 3));
        Assert.Equal("3x3", GridLayoutCatalog.EnsureFits("2x2", 9));
        Assert.Equal(5, GridLayoutCatalog.Get(GridLayoutCatalog.EnsureFits("2x2", 5)).Cells.Count);
    }

    [Fact]
    public void CellPixelSize_ReflectsTheCellShape_AndNeverDropsBelowTwo()
    {
        var (w, h) = GridLayoutCatalog.Get("3-cols").GetCellPixelSize(0, 1280, 720);
        Assert.Equal((427, 720), (w, h));
        var (tw, th) = GridLayoutCatalog.Grid2x2.GetCellPixelSize(0, 1, 1);
        Assert.Equal((2, 2), (tw, th));
    }

    [Fact]
    public void Composite_ThreeCols_PlacesLayersAndLeavesGutterBackground()
    {
        const int w = 600, h = 300;
        var layout = GridLayoutCatalog.Get("3-cols");
        var frames = new[] { SKColors.Red, SKColors.Green, SKColors.Blue }.Select(c =>
        {
            var bmp = new SKBitmap(10, 10);
            using var cv = new SKCanvas(bmp);
            cv.Clear(c);
            return bmp;
        }).ToList();
        var rects = layout.GetCellRects(w, h, 0.05, frames.Count);   // 15px gap
        using var output = Compositor.Composite(w, h, frames, rects);

        Assert.Equal(SKColors.Red, output.GetPixel(100, 150));
        Assert.Equal(SKColors.Green, output.GetPixel(300, 150));
        Assert.Equal(SKColors.Blue, output.GetPixel(500, 150));
        Assert.Equal(SKColors.Black, output.GetPixel(200, 150));   // inside the gap between cells 0 and 1
    }
}
