using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>Renders a static placeholder frame for audio-only layers' grid cell.</summary>
public static class PlaceholderRenderer
{
    public static SKBitmap CreateAudioOnlyPlaceholder(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(40, 40, 40));

        using var paint = new SKPaint { Color = new SKColor(90, 160, 220), StrokeWidth = 3, IsAntialias = true };
        float centerY = height / 2f;
        int bars = 12;
        for (int i = 0; i < bars; i++)
        {
            float x = (i + 0.5f) * width / bars;
            float barHeight = height * 0.15f * (1 + (i % 3));
            canvas.DrawLine(x, centerY - barHeight / 2, x, centerY + barHeight / 2, paint);
        }

        return bitmap;
    }
}
