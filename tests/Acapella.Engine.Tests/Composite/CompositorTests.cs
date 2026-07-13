using Acapella.Engine.Composite;
using SkiaSharp;

namespace Acapella.Engine.Tests.Composite;

public class CompositorTests
{
    private static SKBitmap SolidColorBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    [Fact]
    public void Composite_FourLayers_PlacesEachInCorrectCell()
    {
        const int canvasWidth = 640;
        const int canvasHeight = 480;

        var colors = new[] { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow };
        var frames = colors.Select(c => SolidColorBitmap(320, 240, c)).ToList();

        var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);
        var output = Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects);

        // Sample the center of each expected cell: top-left, top-right, bottom-left, bottom-right.
        AssertPixelNear(output, canvasWidth / 4, canvasHeight / 4, SKColors.Red);
        AssertPixelNear(output, canvasWidth * 3 / 4, canvasHeight / 4, SKColors.Green);
        AssertPixelNear(output, canvasWidth / 4, canvasHeight * 3 / 4, SKColors.Blue);
        AssertPixelNear(output, canvasWidth * 3 / 4, canvasHeight * 3 / 4, SKColors.Yellow);
    }

    [Fact]
    public void Composite_FewerThanFourLayers_DoesNotAssumeFourCells()
    {
        const int canvasWidth = 640;
        const int canvasHeight = 480;

        var frames = new List<SKBitmap> { SolidColorBitmap(320, 240, SKColors.Red), SolidColorBitmap(320, 240, SKColors.Green) };
        var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);

        Assert.Equal(2, cellRects.Count);

        var output = Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects);

        AssertPixelNear(output, canvasWidth / 4, canvasHeight / 4, SKColors.Red);
        AssertPixelNear(output, canvasWidth * 3 / 4, canvasHeight / 4, SKColors.Green);
        // Bottom half should remain the cleared background (untouched, no crash from a
        // hardcoded "always 4 cells" assumption).
        AssertPixelNear(output, canvasWidth / 4, canvasHeight * 3 / 4, SKColors.Black);
    }

    [Fact]
    public void Composite_AudioOnlyPlaceholder_FillsCellWithoutCrashing()
    {
        const int canvasWidth = 640;
        const int canvasHeight = 480;

        var frames = new List<SKBitmap>
        {
            SolidColorBitmap(320, 240, SKColors.Red),
            PlaceholderRenderer.CreateAudioOnlyPlaceholder(320, 240),
        };
        var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);

        var exception = Record.Exception(() => Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects));

        Assert.Null(exception);
    }

    /// <summary>Regression test for v5 P2 task 3: with labels supplied, each cell's border must be
    /// drawn in that cell's own palette color -- checked at a pixel just inside the border stroke,
    /// away from the corner where two borders and a background could ambiguously overlap.</summary>
    [Fact]
    public void Composite_WithLabels_DrawsEachCellsBorderInItsOwnColor()
    {
        const int canvasWidth = 640;
        const int canvasHeight = 480;

        var frames = new List<SKBitmap> { SolidColorBitmap(320, 240, SKColors.White), SolidColorBitmap(320, 240, SKColors.White) };
        var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);
        var labels = new List<CellLabel?>
        {
            new CellLabel(SKColors.Red, "Layer 1"),
            new CellLabel(SKColors.Blue, "Layer 2"),
        };

        var output = Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects, labels);

        // Just inside the left edge of each cell, vertically centered -- inside the border stroke,
        // away from the top-left name tag and away from any corner.
        AssertPixelNear(output, (int)cellRects[0].Left + 1, canvasHeight / 4, SKColors.Red);
        AssertPixelNear(output, (int)cellRects[1].Left + 1, canvasHeight / 4, SKColors.Blue);
    }

    /// <summary>Regression test for v5 P2 task 3: omitting labels (export's path) must produce no
    /// overlay pixels at all -- the cell interior stays exactly the source frame's color, right up
    /// to its edge.</summary>
    [Fact]
    public void Composite_WithoutLabels_DrawsNoOverlay()
    {
        const int canvasWidth = 640;
        const int canvasHeight = 480;

        var frames = new List<SKBitmap> { SolidColorBitmap(320, 240, SKColors.White) };
        var cellRects = Layout2x2Provider.GetCellRects(canvasWidth, canvasHeight, frames.Count);

        var output = Compositor.Composite(canvasWidth, canvasHeight, frames, cellRects, labels: null);

        AssertPixelNear(output, (int)cellRects[0].Left + 1, canvasHeight / 4, SKColors.White);
        AssertPixelNear(output, (int)cellRects[0].Left + 1, (int)cellRects[0].Top + 1, SKColors.White);
    }

    private static void AssertPixelNear(SKBitmap bitmap, int x, int y, SKColor expected)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(Math.Abs(actual.Red - expected.Red) < 10, $"Red mismatch at ({x},{y}): expected {expected}, got {actual}");
        Assert.True(Math.Abs(actual.Green - expected.Green) < 10, $"Green mismatch at ({x},{y}): expected {expected}, got {actual}");
        Assert.True(Math.Abs(actual.Blue - expected.Blue) < 10, $"Blue mismatch at ({x},{y}): expected {expected}, got {actual}");
    }
}
