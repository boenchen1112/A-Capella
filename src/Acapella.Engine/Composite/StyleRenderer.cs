using SkiaSharp;

namespace Acapella.Engine.Composite;

/// <summary>
/// Draws the project's background and watermark onto composite frames. Built once per canvas size
/// and style (the background image is decoded and pre-rendered to canvas size here, not per frame)
/// and then used for every frame of a preview refresh or an export. Thread-safe: the preview's
/// frame thread draws while the UI thread may replace and dispose it, so after Dispose the Draw
/// methods quietly do nothing.
/// </summary>
public sealed class StyleRenderer : IDisposable
{
    private readonly object _lock = new();
    private readonly int _width;
    private readonly int _height;
    private readonly SKColor _backgroundColor;
    private SKBitmap? _backgroundBitmap;   // color + image, pre-rendered to canvas size; null = plain color
    private readonly WatermarkSettings _watermark;
    private SKTypeface? _typeface;
    private bool _ownsTypeface;
    private bool _disposed;

    /// <summary>typeface, when given, is used for the watermark instead of looking up its FontFamily
    /// (the caller keeps ownership of it). Mainly for tests and hosts without system fonts.</summary>
    public StyleRenderer(CompositeStyle style, int canvasWidth, int canvasHeight, SKTypeface? typeface = null)
    {
        _width = canvasWidth;
        _height = canvasHeight;
        _watermark = style.Watermark;
        _backgroundColor = ParseColor(style.Background.Color, SKColors.Black);
        _backgroundBitmap = RenderBackgroundImage(style.Background);
        if (_watermark.IsVisible)
        {
            _ownsTypeface = typeface is null;
            _typeface = typeface ?? SKTypeface.FromFamilyName(_watermark.FontFamily) ?? SKTypeface.Default;
        }
    }

    /// <summary>Fills the canvas with the background (color, plus the image if one is set and loadable).</summary>
    public void DrawBackground(SKCanvas canvas)
    {
        lock (_lock)
        {
            if (_disposed) { canvas.Clear(SKColors.Black); return; }
            canvas.Clear(_backgroundColor);
            if (_backgroundBitmap is not null)
                canvas.DrawBitmap(_backgroundBitmap, 0, 0);
        }
    }

    /// <summary>Draws the watermark, if it is enabled and has text. Call after the cells.</summary>
    public void DrawWatermark(SKCanvas canvas)
    {
        lock (_lock)
        {
            if (_disposed || !_watermark.IsVisible || _typeface is null) return;

            float size = (float)(Math.Clamp(_watermark.SizeFraction, WatermarkSettings.MinSizeFraction, WatermarkSettings.MaxSizeFraction) * _height);
            using var font = new SKFont(_typeface, size);
            string text = _watermark.Text.Trim();
            font.MeasureText(text, out var bounds);
            float margin = Math.Min(_width, _height) * 0.025f;
            float strokeWidth = (float)(Math.Max(0, _watermark.StrokeFraction) * size);

            // bounds is relative to the baseline origin (Top is negative), so position by the box it spans.
            float x = _watermark.Corner switch
            {
                WatermarkCorner.TopLeft or WatermarkCorner.BottomLeft => margin - bounds.Left,
                WatermarkCorner.TopRight or WatermarkCorner.BottomRight => _width - margin - bounds.Right,
                _ => (_width - bounds.Width) / 2 - bounds.Left,
            };
            float y = _watermark.Corner switch
            {
                WatermarkCorner.TopLeft or WatermarkCorner.TopRight => margin - bounds.Top,
                WatermarkCorner.BottomLeft or WatermarkCorner.BottomRight => _height - margin - bounds.Bottom,
                _ => (_height - bounds.Height) / 2 - bounds.Top,
            };

            byte alpha = (byte)Math.Clamp(Math.Round(_watermark.Opacity * 255), 0, 255);
            using var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
            canvas.SaveLayer(layerPaint);

            if (strokeWidth > 0)
            {
                using var stroke = new SKPaint
                {
                    Color = ParseColor(_watermark.StrokeColor, SKColors.Black),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = strokeWidth,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true,
                };
                canvas.DrawText(text, x, y, font, stroke);
            }

            using var fill = new SKPaint { Color = ParseColor(_watermark.FillColor, SKColors.White), IsAntialias = true };
            if (_watermark.Shadow)
                fill.ImageFilter = SKImageFilter.CreateDropShadow(size * 0.04f, size * 0.04f, size * 0.03f, size * 0.03f, SKColors.Black.WithAlpha(180));
            canvas.DrawText(text, x, y, font, fill);

            canvas.Restore();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = null;
            if (_ownsTypeface) _typeface?.Dispose();
            _typeface = null;
        }
    }

    internal static SKColor ParseColor(string? value, SKColor fallback) =>
        value is not null && SKColor.TryParse(value, out var color) ? color.WithAlpha(255) : fallback;

    /// <summary>The color plus image pre-rendered to canvas size, or null when there is no usable image.</summary>
    private SKBitmap? RenderBackgroundImage(BackgroundSettings background)
    {
        if (string.IsNullOrWhiteSpace(background.ImagePath) || !File.Exists(background.ImagePath)) return null;

        SKBitmap? source;
        try { source = SKBitmap.Decode(background.ImagePath); }
        catch (Exception) { return null; }   // unreadable file: fall back to the plain color
        if (source is null) return null;

        using (source)
        {
            var target = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using var canvas = new SKCanvas(target);
            canvas.Clear(_backgroundColor);

            if (background.ImageMode == BackgroundImageMode.Tile)
            {
                // Tiles keep their native size, shrunk if one would be bigger than a third of the canvas.
                float scale = Math.Min(1f, _width / 3f / source.Width);
                using var image = SKImage.FromBitmap(source);
                using var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(scale, scale));
                using var paint = new SKPaint { Shader = shader };
                canvas.DrawRect(0, 0, _width, _height, paint);
            }
            else
            {
                canvas.DrawBitmap(source, new SKRect(0, 0, _width, _height));
            }
            return target;
        }
    }
}
