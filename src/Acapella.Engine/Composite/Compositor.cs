using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>
/// Composites frames into their assigned cell rectangles on a canvas. Deliberately has no
/// knowledge of "2x2" or any specific layout — it iterates whatever (frame, cellRect) pairs it's
/// given, per the build plan's Data Model Rule. Layout2x2Provider is the only piece that knows
/// about the fixed 2x2 grid.
/// </summary>
public static class Compositor
{
    public static SKBitmap Composite(int canvasWidth, int canvasHeight, IReadOnlyList<SKBitmap> frames, IReadOnlyList<SKRect> cellRects)
    {
        var output = new SKBitmap(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Black);

        int cellCount = Math.Min(frames.Count, cellRects.Count);
        for (int i = 0; i < cellCount; i++)
        {
            canvas.DrawBitmap(frames[i], cellRects[i]);
        }

        return output;
    }
}
