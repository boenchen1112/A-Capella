using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>
/// Composites frames into their assigned cell rectangles on a canvas. Deliberately has no
/// knowledge of "2x2" or any specific layout — it iterates whatever (frame, cellRect) pairs it's
/// given, per the build plan's Data Model Rule. Layout2x2Provider is the only piece that knows
/// about the fixed 2x2 grid.
/// </summary>
/// <summary>Per-cell color + name for the grid-identification overlay (v5 P2 task 3). Aligned
/// index-for-index with the frames/cellRects passed to Composite -- pass null for a cell to draw
/// no overlay on just that one (e.g. an audio-only placeholder still gets a border/tag like any
/// other cell in practice, so this is mainly a hook, not a commonly-null case).</summary>
public readonly record struct CellLabel(SKColor Color, string Name);

public static class Compositor
{
    private const float BorderThickness = 3f;
    private const float TagFontSize = 14f;
    private const float TagPadding = 4f;

    /// <summary>labels is optional and off by default: export never passes it (export output must
    /// never include the overlay), and preview only passes it when its "show layer labels" toggle
    /// is on.</summary>
    public static SKBitmap Composite(int canvasWidth, int canvasHeight, IReadOnlyList<SKBitmap> frames, IReadOnlyList<SKRect> cellRects, IReadOnlyList<CellLabel?>? labels = null)
    {
        var output = new SKBitmap(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Black);

        int cellCount = Math.Min(frames.Count, cellRects.Count);
        for (int i = 0; i < cellCount; i++)
        {
            canvas.DrawBitmap(frames[i], cellRects[i]);

            CellLabel? label = labels is not null && i < labels.Count ? labels[i] : null;
            if (label is { } l)
                DrawCellOverlay(canvas, cellRects[i], l);
        }

        return output;
    }

    private static void DrawCellOverlay(SKCanvas canvas, SKRect cellRect, CellLabel label)
    {
        using var borderPaint = new SKPaint
        {
            Color = label.Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = BorderThickness,
            IsAntialias = true,
        };
        // Inset by half the stroke width so the border draws fully inside the cell (a stroke
        // centered exactly on the rect edge would bleed half its width into the neighboring cell).
        float inset = BorderThickness / 2f;
        canvas.DrawRect(new SKRect(cellRect.Left + inset, cellRect.Top + inset, cellRect.Right - inset, cellRect.Bottom - inset), borderPaint);

        using var font = new SKFont(SKTypeface.Default, TagFontSize);
        float textWidth = font.MeasureText(label.Name, out _);
        var tagRect = new SKRect(cellRect.Left, cellRect.Top, cellRect.Left + textWidth + TagPadding * 2, cellRect.Top + TagFontSize + TagPadding * 2);

        using var tagBackgroundPaint = new SKPaint { Color = label.Color, Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(tagRect, tagBackgroundPaint);

        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(label.Name, tagRect.Left + TagPadding, tagRect.Top + TagPadding + TagFontSize * 0.8f, font, textPaint);
    }
}
