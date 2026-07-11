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

    private static void AssertPixelNear(SKBitmap bitmap, int x, int y, SKColor expected)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(Math.Abs(actual.Red - expected.Red) < 10, $"Red mismatch at ({x},{y}): expected {expected}, got {actual}");
        Assert.True(Math.Abs(actual.Green - expected.Green) < 10, $"Green mismatch at ({x},{y}): expected {expected}, got {actual}");
        Assert.True(Math.Abs(actual.Blue - expected.Blue) < 10, $"Blue mismatch at ({x},{y}): expected {expected}, got {actual}");
    }
}
