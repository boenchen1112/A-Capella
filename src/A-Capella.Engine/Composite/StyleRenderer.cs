using SkiaSharp;

namespace A_Capella.Engine.Composite;

/// <summary>
/// Draws the project's background onto composite frames. Built once per canvas size
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
    private bool _disposed;

    public StyleRenderer(CompositeStyle style, int canvasWidth, int canvasHeight)
    {
        _width = canvasWidth;
        _height = canvasHeight;
        _backgroundColor = ParseColor(style.Background.Color, SKColors.Black);
        _backgroundBitmap = RenderBackgroundImage(style.Background);
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

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _backgroundBitmap?.Dispose();
            _backgroundBitmap = null;
        }
    }

    internal static SKColor ParseColor(string? value, SKColor fallback) =>
        value is not null && SKColor.TryParse(value, out var color) ? color.WithAlpha(255) : fallback;

    /// <summary>The color plus image pre-rendered to canvas size, or null when there is no usable image.</summary>
    private SKBitmap? RenderBackgroundImage(BackgroundSettings background)
    {
        if (string.IsNullOrWhiteSpace(background.ImagePath) || !File.Exists(background.ImagePath)) return null;

        SKBitmap? source;
        try { source = SKBitmap.Decode(File.ReadAllBytes(background.ImagePath)); }   // bytes, not the path: decoding by path keeps the file mapped and locked
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
